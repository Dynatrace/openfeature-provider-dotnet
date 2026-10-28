using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Dynatrace.OpenFeature;

/// <summary>
/// Manages periodic polling of flag configurations at a fixed, start-anchored cadence, plus SSE-triggered
/// out-of-band fetches.
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
/// what grace exists to surface. A contended SSE re-fetch is instead deferred and coalesced, keeping the greatest
/// announced <c>Last-Modified</c>, and issued once the slot frees unless the completed fetch already satisfied it.
/// </para>
/// </remarks>
internal sealed class PollingManager : IDisposable
{
    /// <summary>Stands in for an announcement without <c>Last-Modified</c>, so it is never coalesced away or dropped.</summary>
    private static readonly DateTimeOffset LastModifiedUnknown = DateTimeOffset.MaxValue;

    private readonly Func<CancellationToken, Task<FetchResult>> _fetchFlags;
    private readonly Func<string, IReadOnlyList<string>> _storeFlags;
    private readonly Action _commitFetch;
    private readonly Func<DateTimeOffset, bool> _isNewerThanApplied;
    private readonly Action<IReadOnlyList<string>> _emitChange;
    private readonly Action _emitSuccess;
    private readonly Action<string> _emitFailure;
    private readonly ILogger? _logger;

    private readonly object _lock = new object();
    private readonly CancellationTokenSource _stopped = new CancellationTokenSource();
    private Timer? _timer;
    private TimeSpan _interval;

    /// <summary>True from the moment a fetch is started until it has settled.</summary>
    private bool _fetchInFlight;

    /// <summary>Pending SSE re-fetch, as the greatest announcement coalesced into it.</summary>
    private DateTimeOffset? _pendingSseRefetch;

    /// <summary>Announcement the running fetch serves; <c>null</c> for a poll tick.</summary>
    private DateTimeOffset? _inFlightSseRefetch;

    /// <param name="fetchFlags">Called to fetch flag configuration JSON from the CDN.</param>
    /// <param name="storeFlags">Called to store the fetched JSON; returns the changed flag keys.</param>
    /// <param name="commitFetch">Called after a successful store to promote the fetcher's staged validators.</param>
    /// <param name="isNewerThanApplied">Whether a <c>Last-Modified</c> is newer than the configuration in force.</param>
    /// <param name="emitChange">Called with the changed flag keys after a successful store.</param>
    /// <param name="emitSuccess">Called after any successful poll (including 304 not-modified).</param>
    /// <param name="emitFailure">Called with a reason when a poll fails.</param>
    /// <param name="interval">The initial poll cadence.</param>
    /// <param name="logger">Optional logger.</param>
    public PollingManager(
        Func<CancellationToken, Task<FetchResult>> fetchFlags,
        Func<string, IReadOnlyList<string>> storeFlags,
        Action commitFetch,
        Func<DateTimeOffset, bool> isNewerThanApplied,
        Action<IReadOnlyList<string>> emitChange,
        Action emitSuccess,
        Action<string> emitFailure,
        TimeSpan interval,
        ILogger? logger = null)
    {
        _fetchFlags = fetchFlags;
        _storeFlags = storeFlags;
        _commitFetch = commitFetch;
        _isNewerThanApplied = isNewerThanApplied;
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
    public void Dispose()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
            _pendingSseRefetch = null;
            _stopped.Cancel();
        }
    }

    /// <summary>
    /// The poll cadence. Setting a new value re-anchors it to now, so a switch to the fast cadence polls within one
    /// short interval.
    /// </summary>
    public TimeSpan Interval
    {
        get
        {
            lock (_lock)
            {
                return _interval;
            }
        }

        set
        {
            lock (_lock)
            {
                if (value == _interval)
                {
                    return;
                }

                _interval = value;
                _timer?.Change(value, value);
            }
        }
    }

    /// <summary>
    /// Out-of-band fetch for an SSE notification: runs now if the slot is free, otherwise it is deferred and
    /// coalesced.
    /// </summary>
    public void RequestOutOfBandFetch(DateTimeOffset? lastModified = null)
    {
        var announced = lastModified ?? LastModifiedUnknown;
        lock (_lock)
        {
            if (_stopped.IsCancellationRequested)
            {
                return;
            }

            if (_fetchInFlight)
            {
                DeferSseRefetch(announced);
                _logger?.LogDebug("SSE re-fetch deferred: a fetch is already in flight");
                return;
            }

            BeginFetch(announced);
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

            BeginFetch(null);
        }
    }

    // Caller holds _lock.
    private void BeginFetch(DateTimeOffset? sseRefetch)
    {
        _fetchInFlight = true;
        _inFlightSseRefetch = sseRefetch;
        _ = Task.Run(RunFetchAsync);
    }

    // Caller holds _lock.
    private void DeferSseRefetch(DateTimeOffset announced)
    {
        if (_pendingSseRefetch is null || announced > _pendingSseRefetch)
        {
            _pendingSseRefetch = announced;
        }
    }

    private async Task RunFetchAsync()
    {
        var cancellationToken = _stopped.Token;

        // no request reached the CDN, so a re-fetch it served is still unsatisfied
        var unanswered = false;
        try
        {
            var result = await _fetchFlags(cancellationToken).ConfigureAwait(false);
            unanswered = result.IsSkipped || result.IsRateLimited;
            HandleResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // only Dispose() cancels this token, so cancellation means stopping, not a poll failure
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
                OnFetchComplete(unanswered);
            }
        }
    }

    // Caller holds _lock.
    private void OnFetchComplete(bool unanswered)
    {
        var issuedFor = _inFlightSseRefetch;
        _inFlightSseRefetch = null;
        _fetchInFlight = false;
        if (_stopped.IsCancellationRequested)
        {
            return;
        }

        if (unanswered)
        {
            // re-issuing now would be skipped again, so it waits for the first fetch after the window
            if (issuedFor is not null)
            {
                DeferSseRefetch(issuedFor.Value);
            }

            return;
        }

        var pending = _pendingSseRefetch;
        if (pending is null)
        {
            return;
        }

        _pendingSseRefetch = null;
        if (pending.Value != LastModifiedUnknown && !_isNewerThanApplied(pending.Value))
        {
            _logger?.LogDebug("deferred SSE re-fetch dropped: the completed fetch already returned that config");
            return;
        }

        _logger?.LogDebug("issuing deferred SSE re-fetch");
        BeginFetch(pending);
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
        _commitFetch();
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
