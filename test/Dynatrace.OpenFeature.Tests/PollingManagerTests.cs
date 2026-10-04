using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Dynatrace.OpenFeature.Tests;

public class PollingManagerTests : IDisposable
{
    private static readonly TimeSpan Timeout2s = TimeSpan.FromSeconds(2);
    private static readonly IReadOnlyList<string> NoKeys = Array.Empty<string>();

    private PollingManager? _manager;

    public void Dispose() => _manager?.Stop();

    private PollingManager Create(
        Func<CancellationToken, Task<FetchResult>> fetchFlags,
        Func<string, IReadOnlyList<string>>? storeFlags = null,
        Action<IReadOnlyList<string>>? emitChange = null,
        Action? emitSuccess = null,
        Action<string>? emitFailure = null,
        TimeSpan? interval = null)
    {
        _manager = new PollingManager(
            fetchFlags,
            storeFlags ?? (_ => NoKeys),
            emitChange ?? (_ => { }),
            emitSuccess ?? (() => { }),
            emitFailure ?? (_ => { }),
            interval ?? TimeSpan.FromMilliseconds(50));
        return _manager;
    }

    private static Func<CancellationToken, Task<FetchResult>> Returning(FetchResult result) => _ => Task.FromResult(result);

    [Fact]
    public async Task SuccessfulPoll_StoresAndEmitsSuccess()
    {
        var succeeded = new Latch();
        var stored = 0;

        Create(
            Returning(new FetchResult(200, "{}", null, null)),
            storeFlags: _ =>
            {
                Interlocked.Increment(ref stored);
                return NoKeys;
            },
            emitSuccess: () =>
            {
                succeeded.CountDown();
            },
            emitFailure: reason => throw new Exception($"unexpected failure: {reason}")).Start();

        Assert.True(await succeeded.WaitAsync(Timeout2s));
        Assert.True(stored >= 1);
    }

    [Fact]
    public async Task NotModified_EmitsSuccessWithoutStore()
    {
        var succeeded = new Latch();

        Create(
            Returning(new FetchResult(304, null, null, null)),
            storeFlags: _ => throw new Exception("StoreFlags should not be called on 304"),
            emitSuccess: () =>
            {
                succeeded.CountDown();
            },
            emitFailure: reason => throw new Exception($"unexpected failure: {reason}")).Start();

        Assert.True(await succeeded.WaitAsync(Timeout2s));
    }

    [Fact]
    public async Task HttpError_EmitsFailure()
    {
        var failed = new Latch();
        string? failureReason = null;

        Create(
            Returning(new FetchResult(503, null, null, null)),
            storeFlags: _ => throw new Exception("StoreFlags should not be called on error"),
            emitSuccess: () => throw new Exception("EmitSuccess should not be called on error"),
            emitFailure: reason =>
            {
                failureReason = reason;
                failed.CountDown();
            }).Start();

        Assert.True(await failed.WaitAsync(Timeout2s));
        Assert.Equal("HTTP 503", failureReason);
    }

    [Fact]
    public async Task EmptyBody_EmitsFailure()
    {
        var failed = new Latch();
        string? failureReason = null;

        Create(
            Returning(new FetchResult(200, string.Empty, null, null)),
            storeFlags: _ => throw new Exception("StoreFlags should not be called on an empty body"),
            emitSuccess: () => throw new Exception("EmitSuccess should not be called on an empty body"),
            emitFailure: reason =>
            {
                failureReason = reason;
                failed.CountDown();
            }).Start();

        Assert.True(await failed.WaitAsync(Timeout2s));
        Assert.Equal("empty response body", failureReason);
    }

    [Fact]
    public async Task ChangedKeys_EmitChange()
    {
        var changed = new Latch();
        IReadOnlyList<string>? capturedKeys = null;

        Create(
            Returning(new FetchResult(200, "{}", null, null)),
            storeFlags: _ => new[] { "flag-a", "flag-b" },
            emitChange: keys =>
            {
                capturedKeys = keys;
                changed.CountDown();
            },
            emitFailure: reason => throw new Exception($"unexpected failure: {reason}")).Start();

        Assert.True(await changed.WaitAsync(Timeout2s));
        Assert.Equal(new[] { "flag-a", "flag-b" }, capturedKeys);
    }

    [Fact]
    public async Task NoChangedKeys_DoesNotEmitChange()
    {
        var succeeded = new Latch();
        var changeCount = 0;

        Create(
            Returning(new FetchResult(200, "{}", null, null)),
            emitChange: _ =>
            {
                Interlocked.Increment(ref changeCount);
            },
            emitSuccess: () =>
            {
                succeeded.CountDown();
            },
            emitFailure: reason => throw new Exception($"unexpected failure: {reason}")).Start();

        Assert.True(await succeeded.WaitAsync(Timeout2s));
        Assert.Equal(0, changeCount);
    }

    [Fact]
    public async Task FetchException_EmitsFailure()
    {
        var failed = new Latch();
        string? failureReason = null;

        Create(
            _ => throw new InvalidOperationException("network error"),
            emitSuccess: () => throw new Exception("EmitSuccess should not be called on exception"),
            emitFailure: reason =>
            {
                failureReason = reason;
                failed.CountDown();
            }).Start();

        Assert.True(await failed.WaitAsync(Timeout2s));
        Assert.Equal("network error", failureReason);
    }

    [Fact]
    public async Task StoreException_EmitsFailure()
    {
        var failed = new Latch();
        string? failureReason = null;

        Create(
            Returning(new FetchResult(200, "{}", null, null)),
            storeFlags: _ => throw new InvalidOperationException("bad config"),
            emitSuccess: () => throw new Exception("EmitSuccess should not be called when the store failed"),
            emitFailure: reason =>
            {
                failureReason = reason;
                failed.CountDown();
            }).Start();

        Assert.True(await failed.WaitAsync(Timeout2s));
        Assert.Equal("bad config", failureReason);
    }

    [Fact]
    public async Task EmitFailureThatThrows_DoesNotCrashPolling()
    {
        var polls = new Latch(3);

        Create(
            _ =>
            {
                polls.CountDown();
                return Task.FromResult(new FetchResult(503, null, null, null));
            },
            emitFailure: _ => throw new InvalidOperationException("listener blew up")).Start();

        Assert.True(await polls.WaitAsync(Timeout2s), "polling should carry on after a failing callback");
    }

    [Fact]
    public async Task Start_DoesNotFetchBeforeTheFirstInterval()
    {
        var fetches = 0;

        Create(
            _ =>
            {
                Interlocked.Increment(ref fetches);
                return Task.FromResult(new FetchResult(304, null, null, null));
            },
            interval: TimeSpan.FromMilliseconds(400)).Start();

        await Task.Delay(150);

        Assert.Equal(0, fetches); // initialization never contends with the first tick
    }

    [Fact]
    public async Task Stop_PreventsFurtherPolling()
    {
        var polls = 0;
        var firstPoll = new Latch();

        Create(_ =>
        {
            Interlocked.Increment(ref polls);
            firstPoll.CountDown();
            return Task.FromResult(new FetchResult(304, null, null, null));
        }).Start();

        Assert.True(await firstPoll.WaitAsync(Timeout2s));
        _manager!.Stop();

        await Task.Delay(100); // let an in-flight poll settle
        var countAfterStop = Volatile.Read(ref polls);
        await Task.Delay(200); // wait to verify no more polls
        Assert.Equal(countAfterStop, Volatile.Read(ref polls));
    }

    [Fact]
    public async Task Stop_CancelsTheInFlightFetchWithoutReportingAFailure()
    {
        var started = new Latch();
        var observedCancellation = new Latch();
        var failures = 0;

        Create(
            async ct =>
            {
                started.CountDown();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    observedCancellation.CountDown();
                    throw;
                }

                return new FetchResult(304, null, null, null);
            },
            emitFailure: _ =>
            {
                Interlocked.Increment(ref failures);
            }).Start();

        Assert.True(await started.WaitAsync(Timeout2s));
        _manager!.Stop();

        Assert.True(await observedCancellation.WaitAsync(Timeout2s));
        await Task.Delay(50);
        Assert.Equal(0, failures); // cancellation means "stopping", not a poll failure
    }

    [Fact]
    public async Task RateLimited_DoesNotEmitFailure()
    {
        // 429 followed by a 304 to confirm we resume normally
        var polls = 0;
        var succeeded = new Latch();

        Create(
            _ => Task.FromResult(Interlocked.Increment(ref polls) == 1
                ? new FetchResult(429, null, null, null)
                : new FetchResult(304, null, null, null)),
            storeFlags: _ => throw new Exception("StoreFlags should not be called on 429"),
            emitSuccess: () =>
            {
                succeeded.CountDown();
            },
            emitFailure: reason => throw new Exception($"429 should not emit failure: {reason}")).Start();

        Assert.True(await succeeded.WaitAsync(Timeout2s));
    }

    [Fact]
    public async Task PollsRepeatedly()
    {
        var succeeded = new Latch(3);

        Create(
            Returning(new FetchResult(304, null, null, null)),
            emitSuccess: () =>
            {
                succeeded.CountDown();
            }).Start();

        Assert.True(await succeeded.WaitAsync(Timeout2s));
    }

    [Fact]
    public async Task ContendedPollTick_IsSkippedAndEmitsNothing()
    {
        // KB 2.1.1: a contended tick must not cancel the in-flight fetch, and must emit
        // neither success nor failure.
        var firstWasCancelled = false;
        var successes = 0;
        var failures = 0;
        var attempts = 0;

        // sampled from inside the in-flight fetch, so the assertions describe exactly the
        // window during which ticks were contending
        var contendedAttempts = -1;
        var contendedSuccesses = -1;
        var contendedFailures = -1;
        var sampled = new Latch();

        Create(
            async ct =>
            {
                var n = Interlocked.Increment(ref attempts);
                if (n == 1)
                {
                    try
                    {
                        // 50ms cadence: roughly eight ticks contend during this window
                        await Task.Delay(400, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        firstWasCancelled = true;
                        throw;
                    }

                    contendedAttempts = Volatile.Read(ref attempts);
                    contendedSuccesses = Volatile.Read(ref successes);
                    contendedFailures = Volatile.Read(ref failures);
                    sampled.CountDown();
                }

                return new FetchResult(304, null, null, null);
            },
            emitSuccess: () =>
            {
                Interlocked.Increment(ref successes);
            },
            emitFailure: _ =>
            {
                Interlocked.Increment(ref failures);
            }).Start();

        Assert.True(await sampled.WaitAsync(TimeSpan.FromSeconds(3)), "the overrunning poll should run to completion, not be cancelled");
        _manager!.Stop();

        Assert.False(firstWasCancelled); // a contended tick must not cancel the in-flight fetch
        Assert.Equal(1, contendedAttempts); // contended ticks must not start a second fetch
        Assert.Equal(0, contendedSuccesses); // skipped ticks must not emit success
        Assert.Equal(0, contendedFailures); // skipped ticks must not emit failure
    }

    [Fact]
    public async Task SkippedFetchResult_EmitsNeitherSuccessNorFailure()
    {
        // CdnFetcher returns Skipped when its own single-flight lock is contended
        var fetched = new Latch(2);
        var successes = 0;
        var failures = 0;

        Create(
            _ =>
            {
                fetched.CountDown();
                return Task.FromResult(FetchResult.Skipped);
            },
            storeFlags: _ => throw new Exception("StoreFlags should not be called on a skipped fetch"),
            emitSuccess: () =>
            {
                Interlocked.Increment(ref successes);
            },
            emitFailure: _ =>
            {
                Interlocked.Increment(ref failures);
            }).Start();

        Assert.True(await fetched.WaitAsync(Timeout2s));
        Assert.Equal(0, successes); // a skipped fetch must not reset the grace timer
        Assert.Equal(0, failures); // a skipped fetch is not a failure
    }
}
