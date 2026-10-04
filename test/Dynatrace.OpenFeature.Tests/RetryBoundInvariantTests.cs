using System;

namespace Dynatrace.OpenFeature.Tests;

/// <summary>
/// Guards the KB section 2.1 invariant: <c>max fetch duration &lt; shortest poll interval</c>.
/// </summary>
/// <remarks>
/// Two polls must never be in flight at once. That is not a coincidence of the current numbers; it is a property
/// any change to the request timeout, the attempt count or the backoff has to preserve. If this test fails, the
/// change broke the bound; fix the numbers, don't relax the test.
/// </remarks>
public class RetryBoundInvariantTests
{
    // Polling is not implemented yet; this is the spec's shortest poll interval (the SSE-disconnected cadence).
    // Replace with the provider's constant once it exists.
    private static readonly TimeSpan ShortestPollInterval = TimeSpan.FromSeconds(10);

    [Fact]
    public void MaxFetchDuration_IsShorterThanTheShortestPollInterval()
    {
        var maxFetch = RetryStrategy.MaxAttempts * CdnFetcher.RequestTimeout + RetryStrategy.MaxTotalBackoff;

        Assert.True(
            maxFetch < ShortestPollInterval,
            $"max fetch duration {maxFetch} must be under the shortest poll interval {ShortestPollInterval} (KB 2.1)");
    }

    [Fact]
    public void CurrentNumbers_MatchTheSpecArithmetic()
    {
        // 4s + 0.12s + 4s = 8.12s < 10s
        Assert.Equal(TimeSpan.FromSeconds(4), CdnFetcher.RequestTimeout);
        Assert.Equal(2, RetryStrategy.MaxAttempts);
        Assert.Equal(TimeSpan.FromMilliseconds(120), RetryStrategy.MaxTotalBackoff);
    }
}
