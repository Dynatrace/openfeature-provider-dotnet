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
/// Scheduled flusher that drains the <see cref="DynatraceMetricsAggregator"/>, serializes the documented Dynatrace
/// evaluation metrics payload, and POSTs it to the configured metrics ingest endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Server-SDK flush triggers (per the evaluation-metrics scoping doc):
/// </para>
/// <list type="bullet">
/// <item><description>periodic: fixed-delay scheduling</description></item>
/// <item><description><see cref="OnRecord"/>: capacity, called by the hook after each evaluation</description></item>
/// <item><description>
/// <see cref="TriggerFlush"/>: version rotation, called when a new config is stored, after sealing the current batch
/// via <see cref="EnqueueSealedBatch"/>
/// </description></item>
/// <item><description><see cref="ShutdownAsync"/>: performs a final flush</description></item>
/// </list>
/// <para>
/// All HTTP work happens on background tasks, one flush at a time, never on the evaluation path.
/// </para>
/// </remarks>
internal sealed class EvaluationMetricsFlusher
{
    internal static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Cooldown applied to out-of-band flushes after a failed delivery, to avoid hammering a struggling endpoint
    /// between periodic flush ticks.
    /// </summary>
    internal static readonly TimeSpan FailedCapacityRetryCooldown = TimeSpan.FromSeconds(30);

    /// <summary>Maximum number of sealed batches held in the queue at any time.</summary>
    internal const int MaxSealedBatches = 10;

    private const string SdkType = "server";

    // Relaxed escaping: the body is JSON on the wire, not embedded in HTML, so there is no reason to \uXXXX-escape
    // characters such as '+' or '<' in flag keys.
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

    // Serialises flushes: periodic, out-of-band, rotation and the final one run one at a time.
    private readonly SemaphoreSlim _flushGate = new SemaphoreSlim(1, 1);
    private readonly CancellationTokenSource _stopped = new CancellationTokenSource();

    private int _started;
    private int _shutdown;
    private int _outOfBandFlushScheduled;
    private int _sealedBatchCount;
    private long _nextOutOfBandFlushAllowedTimestamp;

    // Pre-drained batches sealed by version-rotation calls to EnqueueSealedBatch, flushed before the live aggregator
    // on every flush cycle to preserve per-version batch ordering. A failed batch is requeued at the front.
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

    /// <summary>Starts periodic flushing. Idempotent; safe to call once per provider lifecycle.</summary>
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

    /// <summary>
    /// Accepts a pre-drained batch for delivery ahead of the next periodic tick. The batch is appended to the
    /// sealed-batch queue and will be delivered before any live-aggregator data on the next flush cycle, preserving
    /// per-version ordering. No-op for empty batches.
    /// </summary>
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

    /// <summary>
    /// Schedules a flush on a background task without blocking the caller. Safe to call before <see cref="Start"/>.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="OnRecord"/>, this does not coalesce with an in-flight flush: each call queues a fresh one.
    /// This is intentional: callers (config rotation, shutdown-style code paths) need the guarantee that their
    /// just-enqueued sealed batch will be picked up promptly even if another flush is still completing. The extra flush
    /// is cheap when there is nothing to deliver.
    /// </remarks>
    public void TriggerFlush()
    {
        if (Volatile.Read(ref _shutdown) == 1)
        {
            return;
        }

        _ = Task.Run(FlushQuietlyAsync);
    }

    /// <summary>
    /// Capacity callback invoked by the hook after each evaluation. If the live aggregate map has crossed the capacity
    /// threshold, schedules an out-of-band flush. Never blocks the calling thread.
    /// </summary>
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
                // Fixed delay: the next tick is measured from the end of the previous flush.
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
        // Flush sealed version-rotation batches first to preserve per-version ordering. try/finally so an unexpected
        // throw from FlushBatchAsync can't drop the batch on the floor: we always either deliver or requeue at the
        // front of the queue.
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

        // Then flush whatever has accumulated in the live aggregator. Same try/finally pattern: if FlushBatchAsync
        // throws after Drain(), we must requeue the snapshot so counts aren't lost.
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

    /// <summary>
    /// Attempts to POST <paramref name="batch"/> to the ingestion endpoint. Returns <c>true</c> if delivered
    /// successfully (or dropped as non-retryable); <c>false</c> if the caller should requeue the batch for the next
    /// flush cycle.
    /// </summary>
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

    /// <summary>
    /// The documented evaluation metrics payload. Property order is the order on the wire; names are camelCase.
    /// </summary>
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
