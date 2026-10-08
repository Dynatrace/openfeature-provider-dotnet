using System;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Dynatrace.OpenFeature;

/// <summary>
/// Bounded retry for transient HTTP failures: one retry after a short jittered delay.
/// </summary>
/// <remarks>
/// <para>
/// Retries on 5xx and network/timeout errors. Does not retry 4xx (429 is handled separately by
/// <see cref="CdnFetcher"/>, and ends the fetch outright).
/// </para>
/// <para>
/// Parameters per KB section 2.4: max attempts 2 (initial attempt plus 1 retry), 100ms backoff before the
/// retry, and +0-20% additive random jitter.
/// </para>
/// <para>
/// One retry, not a loop: a fetch must not outlive the shortest poll interval (KB section 2.1), so beyond
/// the single retry recovery is the next poll tick's job. <c>RetryBoundInvariantTests</c> guards the
/// arithmetic.
/// </para>
/// </remarks>
internal sealed class RetryStrategy
{
    internal const int MaxAttempts = 2;
    internal const double MaxJitterFactor = 0.20;

    internal static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>Worst-case sleep across a whole fetch; an input to the fetch-duration invariant.</summary>
    internal static readonly TimeSpan MaxTotalBackoff =
        TimeSpan.FromMilliseconds((MaxAttempts - 1) * BaseDelay.TotalMilliseconds * (1 + MaxJitterFactor));

    private readonly Random _random;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger? _logger;

    public RetryStrategy(ILogger? logger = null)
        : this(new Random(), Task.Delay, logger)
    {
    }

    // visible for testing: a delay seam so tests need not wait
    public RetryStrategy(Random random, Func<TimeSpan, CancellationToken, Task> delay, ILogger? logger = null)
    {
        _random = random;
        _delay = delay;
        _logger = logger;
    }

    internal static bool ShouldRetry(int status) => status >= 500 && status < 600;

    /// <summary>
    /// Executes the given request with retry logic.
    /// </summary>
    /// <returns>The result of a successful or non-retryable request.</returns>
    /// <exception cref="HttpRequestException">All attempts failed with network errors.</exception>
    /// <exception cref="TimeoutException">All attempts timed out.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled, including during the backoff.
    /// </exception>
    public async Task<FetchResult> ExecuteAsync(
        Func<CancellationToken, Task<FetchResult>> request, CancellationToken cancellationToken = default)
    {
        Exception? lastNetworkError = null;
        FetchResult? lastResult = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                var result = await request(cancellationToken).ConfigureAwait(false);
                lastResult = result;

                if (!ShouldRetry(result.StatusCode))
                {
                    return result;
                }

                _logger?.LogWarning(
                    "attempt {Attempt}/{MaxAttempts} returned HTTP {StatusCode}{Outcome}",
                    attempt,
                    MaxAttempts,
                    result.StatusCode,
                    attempt < MaxAttempts ? "; retrying" : "; giving up");
            }
            catch (Exception e) when (e is HttpRequestException || e is TimeoutException)
            {
                lastNetworkError = e;
                _logger?.LogWarning(
                    "attempt {Attempt}/{MaxAttempts} failed: {Message}{Outcome}",
                    attempt,
                    MaxAttempts,
                    e.Message,
                    attempt < MaxAttempts ? "; retrying" : "; giving up");
            }

            if (attempt < MaxAttempts)
            {
                var jitter = TimeSpan.FromMilliseconds(BaseDelay.TotalMilliseconds * _random.NextDouble() * MaxJitterFactor);
                await _delay(BaseDelay + jitter, cancellationToken).ConfigureAwait(false);
            }
        }

        if (lastResult is not null)
        {
            return lastResult;
        }

        if (lastNetworkError is not null)
        {
            ExceptionDispatchInfo.Capture(lastNetworkError).Throw();
        }

        throw new HttpRequestException("all retry attempts failed");
    }
}
