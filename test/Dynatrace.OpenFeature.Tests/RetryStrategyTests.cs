using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Dynatrace.OpenFeature.Tests;

public class RetryStrategyTests
{
    private readonly List<TimeSpan> _sleeps = new List<TimeSpan>();

    private RetryStrategy Strategy() => new RetryStrategy(new Random(42), (delay, _) =>
    {
        _sleeps.Add(delay);
        return Task.CompletedTask;
    });

    private static Func<CancellationToken, Task<FetchResult>> Returning(FetchResult result) => _ => Task.FromResult(result);

    [Fact]
    public async Task SuccessOnFirstAttempt_DoesNotRetry()
    {
        var result = await Strategy().ExecuteAsync(Returning(new FetchResult(200, "{}", null, null)));

        Assert.Equal(200, result.StatusCode);
        Assert.Empty(_sleeps);
    }

    [Fact]
    public async Task RetriesOn5xx_UpToMaxAttempts()
    {
        var result = await Strategy().ExecuteAsync(Returning(new FetchResult(503, null, null, null)));

        Assert.Equal(503, result.StatusCode);
        Assert.Single(_sleeps); // MaxAttempts - 1 backoffs
    }

    [Theory]
    [InlineData(400)]
    [InlineData(304)]
    [InlineData(429)]
    public async Task DoesNotRetryOnNon5xx(int status)
    {
        var result = await Strategy().ExecuteAsync(Returning(new FetchResult(status, null, null, null)));

        Assert.Equal(status, result.StatusCode);
        Assert.Empty(_sleeps);
    }

    [Fact]
    public async Task RetriesOnNetworkError_ThenRethrows()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<HttpRequestException>(() => Strategy().ExecuteAsync(_ =>
        {
            attempts++;
            throw new HttpRequestException("connection refused");
        }));

        Assert.Equal(RetryStrategy.MaxAttempts, attempts);
    }

    [Fact]
    public async Task RetriesOnTimeout_ThenRethrows()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<TimeoutException>(() => Strategy().ExecuteAsync(_ =>
        {
            attempts++;
            throw new TimeoutException("slow");
        }));

        Assert.Equal(RetryStrategy.MaxAttempts, attempts);
    }

    [Fact]
    public async Task RecoversAfterTransientFailureOnTheSingleRetry()
    {
        var attempts = 0;

        var result = await Strategy().ExecuteAsync(_ =>
            Task.FromResult(++attempts < 2 ? new FetchResult(503, null, null, null) : new FetchResult(200, "{}", null, null)));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(2, attempts);
        Assert.Single(_sleeps);
    }

    [Fact]
    public async Task CapsAtTwoAttemptsWithOneBackoff()
    {
        // KB 2.4: one retry, not a loop; the fetch must stay short enough that it cannot
        // outlive the shortest poll interval (KB 2.1).
        Assert.Equal(2, RetryStrategy.MaxAttempts);
        var attempts = 0;

        await Strategy().ExecuteAsync(_ =>
        {
            attempts++;
            return Task.FromResult(new FetchResult(500, null, null, null));
        });

        Assert.Equal(2, attempts);
        Assert.Single(_sleeps);
    }

    [Fact]
    public async Task Backoff_IsFixedBaseDelayPlusJitter()
    {
        await Strategy().ExecuteAsync(Returning(new FetchResult(500, null, null, null)));

        var delay = Assert.Single(_sleeps);
        Assert.True(delay >= RetryStrategy.BaseDelay, $"delay {delay} should be at least the base delay");
        Assert.True(
            delay <= RetryStrategy.BaseDelay * (1 + RetryStrategy.MaxJitterFactor),
            $"delay {delay} should not exceed base delay + 20% jitter");
    }

    [Fact]
    public async Task CancelledDuringBackoff_Propagates()
    {
        using var cts = new CancellationTokenSource();
        var strategy = new RetryStrategy(new Random(42), (_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            strategy.ExecuteAsync(Returning(new FetchResult(500, null, null, null)), cts.Token));
    }

    [Fact]
    public async Task ReturnsLastResult_WhenAllAttemptsRetryable()
    {
        var attempts = 0;

        var result = await Strategy().ExecuteAsync(_ => Task.FromResult(new FetchResult(500 + ++attempts, null, null, null)));

        // should return the last 5xx result, not throw
        Assert.True(result.StatusCode >= 500);
    }

    [Theory]
    [InlineData(200, false)]
    [InlineData(304, false)]
    [InlineData(400, false)]
    [InlineData(404, false)]
    [InlineData(429, false)]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(599, true)]
    [InlineData(600, false)]
    public void ShouldRetry_OnlyFor5xx(int status, bool expected)
    {
        Assert.Equal(expected, RetryStrategy.ShouldRetry(status));
    }
}
