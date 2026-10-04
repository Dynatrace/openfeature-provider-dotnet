using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Dynatrace.OpenFeature;

/// <summary>
/// Manages periodic polling of flag configurations at a fixed, start-anchored cadence.
/// </summary>
/// <remarks>
/// <para>
/// A timer fires every interval, anchored to start rather than to how long a poll takes, so retry/backoff inside a
/// poll never stretches the cadence, and hands the actual work to a background task.
/// </para>
/// <para>
/// Single-flight (KB section 2.1.1): at most one fetch runs at a time, and a contended tick is never
/// cancelled-and-restarted. It is skipped, emitting neither success nor failure, so it does not reset the staleness
/// grace timer. Reporting it as a success would hold the provider in <c>Ready</c> for a whole CDN outage, hiding
/// what grace exists to surface.
/// </para>
/// </remarks>
internal sealed class PollingManager
{
    private readonly Func<CancellationToken, Task<FetchResult>> _fetchFlags;
    private readonly Func<string, IReadOnlyList<string>> _storeFlags;
    private readonly Action<IReadOnlyList<string>> _emitChange;
    private readonly Action _emitSuccess;
    private readonly Action<string> _emitFailure;
    private readonly TimeSpan _interval;
    private readonly ILogger? _logger;

    private readonly object _lock = new object();
    private readonly CancellationTokenSource _stopped = new CancellationTokenSource();
    private Timer? _timer;

    /// <summary>True from the moment a fetch is started until it has settled.</summary>
    private bool _fetchInFlight;

    /// <param name="fetchFlags">Called to fetch flag configuration JSON from the CDN.</param>
    /// <param name="storeFlags">Called to store the fetched JSON; returns the changed flag keys.</param>
    /// <param name="emitChange">Called with the changed flag keys after a successful store.</param>
    /// <param name="emitSuccess">Called after any successful poll (including 304 not-modified).</param>
    /// <param name="emitFailure">Called with a reason when a poll fails.</param>
    /// <param name="interval">The poll cadence.</param>
    /// <param name="logger">Optional logger.</param>
    public PollingManager(
        Func<CancellationToken, Task<FetchResult>> fetchFlags,
        Func<string, IReadOnlyList<string>> storeFlags,
        Action<IReadOnlyList<string>> emitChange,
        Action emitSuccess,
        Action<string> emitFailure,
        TimeSpan interval,
        ILogger? logger = null)
    {
        _fetchFlags = fetchFlags;
        _storeFlags = storeFlags;
        _emitChange = emitChange;
        _emitSuccess = emitSuccess;
        _emitFailure = emitFailure;
        _interval = interval;
        _logger = logger;
    }

    /// <summary>
    /// Starts polling. Must be called after the initial fetch/store completes (KB section 3.1 step 4), so
    /// initialization never contends with the first tick.
    /// </summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_timer is not null || _stopped.IsCancellationRequested)
            {
                return;
            }

            // The tick body only hands work to a background task, so the timer callback itself never blocks.
            _timer = new Timer(_ => OnTick(), null, _interval, _interval);
        }
    }

    /// <summary>
    /// Stops polling and cancels any in-flight fetch.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
            _stopped.Cancel();
        }
    }

    /// <summary>Poll tick. Skipped if a fetch is in flight: that fetch already does what this tick wanted.</summary>
    private void OnTick()
    {
        lock (_lock)
        {
            if (_stopped.IsCancellationRequested)
            {
                return;
            }

            if (_fetchInFlight)
            {
                _logger?.LogDebug("poll tick skipped: a fetch is already in flight");
                return;
            }

            _fetchInFlight = true;
        }

        _ = Task.Run(RunFetchAsync);
    }

    private async Task RunFetchAsync()
    {
        var cancellationToken = _stopped.Token;
        try
        {
            var result = await _fetchFlags(cancellationToken).ConfigureAwait(false);
            HandleResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Only Stop() cancels this token now that contended ticks are skipped rather than cancelled, so
            // cancellation means "stopping", not a poll failure.
            _logger?.LogDebug("poll cancelled (shutting down)");
        }
        catch (Exception e)
        {
            _logger?.LogWarning(e, "poll failed");
            SafeEmitFailure(e.Message);
        }
        finally
        {
            lock (_lock)
            {
                _fetchInFlight = false;
            }
        }
    }

    private void HandleResult(FetchResult result)
    {
        if (result.IsSkipped)
        {
            _logger?.LogDebug("fetch skipped: no request was made");
            return;
        }

        if (result.IsNotModified)
        {
            _logger?.LogDebug("flags not modified");
            _emitSuccess();
            return;
        }

        // 429: rate limited; still serving cached values (spec 2.3)
        if (result.IsRateLimited)
        {
            _logger?.LogDebug("rate limited; serving cached values");
            return;
        }

        if (!result.IsSuccess)
        {
            _emitFailure($"HTTP {result.StatusCode}");
            return;
        }

        var body = result.Body;
        if (string.IsNullOrEmpty(body))
        {
            _emitFailure("empty response body");
            return;
        }

        var changedKeys = _storeFlags(body!);
        _emitSuccess();

        if (changedKeys.Count > 0)
        {
            _logger?.LogInformation("flag configuration changed: {ChangedKeys}", changedKeys);
            _emitChange(changedKeys);
        }
    }

    private void SafeEmitFailure(string reason)
    {
        try
        {
            _emitFailure(reason);
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "failed to report poll failure");
        }
    }
}
