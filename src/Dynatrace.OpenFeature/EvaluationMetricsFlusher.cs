using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Dynatrace.OpenFeature;

/// <summary>
/// Drains the aggregator and POSTs it to the metrics endpoint: periodically, at capacity (<see cref="OnRecord"/>), on
/// version rotation (<see cref="TriggerFlush"/>) and on shutdown. One flush at a time, off the evaluation path.
/// </summary>
internal sealed class EvaluationMetricsFlusher
{
    internal static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(10);

    // suppresses capacity flushes after a failed delivery
    internal static readonly TimeSpan FailedCapacityRetryCooldown = TimeSpan.FromSeconds(30);

    internal const int MaxSealedBatches = 10;

    private const string SdkType = "server";

    // not embedded in HTML, so no need to escape '+' or '<' in flag keys
    private static readonly JsonSerializerOptions PayloadSerializerOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly DynatraceMetricsAggregator _aggregator;
    private readonly HttpClient _httpClient;
    private readonly Uri _metricsUri;
    private readonly AuthenticationHeaderValue _authorization;
    private readonly TimeSpan _flushInterval;
    private readonly int _capacityThreshold;
    private readonly string _sdkVersion;
    private readonly string _providerName;
    private readonly ILogger? _logger;

    // one flush at a time, whatever triggered it
    private readonly SemaphoreSlim _flushGate = new SemaphoreSlim(1, 1);
    private readonly CancellationTokenSource _stopped = new CancellationTokenSource();

    private int _started;
    private int _shutdown;
    private int _outOfBandFlushScheduled;
    private int _sealedBatchCount;
    private long _nextOutOfBandFlushAllowedTimestamp;

    // sealed on version rotation; sent before the live map, failures requeued at the front
    private readonly LinkedList<DynatraceMetricsAggregator.DrainedBatch> _sealedBatches =
        new LinkedList<DynatraceMetricsAggregator.DrainedBatch>();

    public EvaluationMetricsFlusher(
        DynatraceMetricsAggregator aggregator,
        HttpClient httpClient,
        string sdkKey,
        Uri metricsUri,
        TimeSpan flushInterval,
        int capacityThreshold,
        string sdkVersion,
        string providerName,
        ILogger? logger = null)
    {
        _aggregator = aggregator;
        _httpClient = httpClient;
        _metricsUri = metricsUri;
        _authorization = new AuthenticationHeaderValue("Bearer", sdkKey);
        _flushInterval = flushInterval;
        _capacityThreshold = capacityThreshold;
        _sdkVersion = sdkVersion;
        _providerName = providerName;
        _logger = logger;
    }

    /// <summary>Starts periodic flushing. Idempotent.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        _ = Task.Run(RunPeriodicFlushAsync);
        _logger?.LogDebug(
            "evaluation metrics flusher started (interval={Interval}, url={MetricsUri})", _flushInterval, _metricsUri);
    }

    /// <summary>Queues a drained batch to send before the live map; empty batches are ignored.</summary>
    public void EnqueueSealedBatch(DynatraceMetricsAggregator.DrainedBatch? batch)
    {
        if (batch is null || batch.IsEmpty)
        {
            return;
        }

        if (Interlocked.Increment(ref _sealedBatchCount) > MaxSealedBatches)
        {
            Interlocked.Decrement(ref _sealedBatchCount);
            _logger?.LogWarning(
                "sealed batch queue full ({MaxSealedBatches} batches); dropping incoming batch for version {Version} to prevent unbounded growth",
                MaxSealedBatches,
                batch.Version);
            return;
        }

        lock (_sealedBatches)
        {
            _sealedBatches.AddLast(batch);
        }
    }

    /// <summary>Schedules a flush without blocking; not coalesced, so a just-sealed batch is always picked up.</summary>
    public void TriggerFlush()
    {
        if (Volatile.Read(ref _shutdown) == 1)
        {
            return;
        }

        _ = Task.Run(FlushQuietlyAsync);
    }

    /// <summary>Called after each evaluation; schedules a flush once the live map reaches the capacity threshold.</summary>
    public void OnRecord()
    {
        if (Volatile.Read(ref _started) == 0 || Volatile.Read(ref _shutdown) == 1)
        {
            return;
        }

        if (_aggregator.Size < _capacityThreshold)
        {
            return;
        }

        if (Stopwatch.GetTimestamp() < Interlocked.Read(ref _nextOutOfBandFlushAllowedTimestamp))
        {
            return;
        }

        ScheduleOutOfBandFlush();
    }

    /// <summary>Performs a final flush and stops periodic flushing. Idempotent.</summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _shutdown, 1) == 1)
        {
            return;
        }

        _stopped.Cancel();

        try
        {
            var finalFlush = FlushQuietlyAsync();
            var completed = await Task.WhenAny(finalFlush, Task.Delay(HttpTimeout + TimeSpan.FromSeconds(2), cancellationToken))
                .ConfigureAwait(false);
            if (completed != finalFlush)
            {
                _logger?.LogWarning("final metrics flush did not complete");
            }
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("final metrics flush did not complete: shutdown was cancelled");
        }
    }

    private async Task RunPeriodicFlushAsync()
    {
        try
        {
            while (!_stopped.IsCancellationRequested)
            {
                // fixed delay: measured from the end of the previous flush
                await Task.Delay(_flushInterval, _stopped.Token).ConfigureAwait(false);
                await FlushQuietlyAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down; the final flush drains whatever is left
        }
    }

    private void ScheduleOutOfBandFlush()
    {
        if (Interlocked.CompareExchange(ref _outOfBandFlushScheduled, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await FlushQuietlyAsync().ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _outOfBandFlushScheduled, 0);
            }
        });
    }

    private async Task FlushQuietlyAsync()
    {
        await _flushGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await FlushAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger?.LogWarning(e, "evaluation metrics flush failed");
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private async Task FlushAsync()
    {
        // sealed batches first, in version order; finally guarantees deliver-or-requeue
        DynatraceMetricsAggregator.DrainedBatch? sealedBatch;
        while ((sealedBatch = PollFirstSealedBatch()) is not null)
        {
            var delivered = false;
            try
            {
                delivered = await FlushBatchAsync(sealedBatch).ConfigureAwait(false);
            }
            finally
            {
                if (delivered)
                {
                    Interlocked.Decrement(ref _sealedBatchCount);
                }
                else
                {
                    OfferFirstSealedBatch(sealedBatch);
                }
            }

            if (!delivered)
            {
                return;
            }
        }

        // then the live map; requeued if delivery fails or throws
        var live = _aggregator.Drain();
        if (live.IsEmpty)
        {
            return;
        }

        var liveDelivered = false;
        try
        {
            liveDelivered = await FlushBatchAsync(live).ConfigureAwait(false);
        }
        finally
        {
            if (!liveDelivered)
            {
                Interlocked.Increment(ref _sealedBatchCount);
                OfferFirstSealedBatch(live);
            }
        }
    }

    private DynatraceMetricsAggregator.DrainedBatch? PollFirstSealedBatch()
    {
        lock (_sealedBatches)
        {
            var first = _sealedBatches.First;
            if (first is null)
            {
                return null;
            }

            _sealedBatches.RemoveFirst();
            return first.Value;
        }
    }

    private void OfferFirstSealedBatch(DynatraceMetricsAggregator.DrainedBatch batch)
    {
        lock (_sealedBatches)
        {
            _sealedBatches.AddFirst(batch);
        }
    }

    /// <summary>POSTs the batch; <c>false</c> means requeue, <c>true</c> means delivered or dropped as non-retryable.</summary>
    private async Task<bool> FlushBatchAsync(DynatraceMetricsAggregator.DrainedBatch batch)
    {
        var body = BuildPayload(batch);
        try
        {
            using var timeout = new CancellationTokenSource(HttpTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, _metricsUri)
            {
                Content = new StringContent(body, Encoding.UTF8),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Authorization = _authorization;

            using var response = await _httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status / 100 == 2)
            {
                Interlocked.Exchange(ref _nextOutOfBandFlushAllowedTimestamp, 0L);
                _logger?.LogDebug("evaluation metrics flush ok (status={Status}, payload bytes={Bytes})", status, body.Length);
                return true;
            }

            if (status == 400)
            {
                _logger?.LogWarning(
                    "evaluation metrics payload rejected with HTTP 400; dropping non-retryable batch (payload bytes={Bytes})",
                    body.Length);
                return true;
            }

            _logger?.LogWarning("evaluation metrics flush returned HTTP {Status}; payload bytes={Bytes}", status, body.Length);
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("evaluation metrics flush timed out after {Timeout}", HttpTimeout);
        }
        catch (Exception e)
        {
            _logger?.LogWarning(e, "evaluation metrics flush HTTP error");
        }

        Interlocked.Exchange(
            ref _nextOutOfBandFlushAllowedTimestamp,
            Stopwatch.GetTimestamp() + (long)(FailedCapacityRetryCooldown.TotalSeconds * Stopwatch.Frequency));
        return false;
    }

    private string BuildPayload(DynatraceMetricsAggregator.DrainedBatch batch)
    {
        var payload = new EvaluationMetricsPayload
        {
            Version = batch.Version,
            SdkType = SdkType,
            SdkVersion = _sdkVersion,
            ProviderName = _providerName,
        };

        foreach (var flag in batch.Evaluations)
        {
            foreach (var aggregate in flag.Value.Values)
            {
                payload.Evaluations.Add(new EvaluationMetricsPayload.Evaluation
                {
                    FlagKey = flag.Key,
                    Variant = aggregate.Variant,
                    EvaluationReason = aggregate.Reason,
                    ErrorCode = aggregate.ErrorCode,
                    TimestampSec = aggregate.TimestampSec,
                    Count = aggregate.Count,
                });
            }
        }

        return JsonSerializer.Serialize(payload, PayloadSerializerOptions);
    }

    // property order is the order on the wire
    internal sealed class EvaluationMetricsPayload
    {
        public string? Version { get; set; }

        public string SdkType { get; set; } = string.Empty;

        public string SdkVersion { get; set; } = string.Empty;

        public string ProviderName { get; set; } = string.Empty;

        public List<Evaluation> Evaluations { get; } = new List<Evaluation>();

        internal sealed class Evaluation
        {
            public string FlagKey { get; set; } = string.Empty;

            public string Variant { get; set; } = string.Empty;

            public string EvaluationReason { get; set; } = string.Empty;

            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? ErrorCode { get; set; }

            public long TimestampSec { get; set; }

            public long Count { get; set; }
        }
    }
}
