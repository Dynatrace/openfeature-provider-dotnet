using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Dynatrace.OpenFeature;

/// <summary>
/// Fetches flag configuration JSON from the CDN.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>ETag / If-None-Match revalidation (304 = skip update).</description></item>
/// <item><description>
/// Last-Modified / If-Modified-Since revalidation; defends against stale CDN nodes by rejecting 200 responses
/// whose <c>Last-Modified</c> is not strictly later than the previously stored value.
/// </description></item>
/// <item><description>Bounded retry on 5xx / network errors (via <see cref="RetryStrategy"/>).</description></item>
/// <item><description>
/// 429 / Retry-After rate limiting; a 429 ends the fetch and is never retried in-place.
/// </description></item>
/// <item><description>Single-flight: a fetch arriving while another is in progress is skipped, not queued.</description></item>
/// </list>
/// </remarks>
internal sealed class CdnFetcher
{
    /// <summary>
    /// Bounds a single attempt. With <see cref="RetryStrategy.MaxAttempts"/> this keeps a whole fetch shorter than
    /// the shortest poll interval (KB section 2.1); changing it means re-checking that invariant, which
    /// <c>RetryBoundInvariantTests</c> guards.
    /// </summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);

    /// <summary>Default Retry-After duration when a 429 has no usable header.</summary>
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(60);

    private readonly Uri _url;
    private readonly HttpClient _httpClient;
    private readonly RetryStrategy _retryStrategy;
    private readonly TimeSpan _requestTimeout;
    private readonly ILogger? _logger;

    // validators sent on the next request; promoted from _pendingState by Commit()
    private volatile CacheState _state = CacheState.Empty;

    // validators of the last 2xx, held until the caller has applied its body
    private volatile CacheState? _pendingState;

    // rate limit tracking (UTC ticks); read outside the lock by IsRateLimited
    private long _rateLimitedUntilTicks;

    private readonly SemaphoreSlim _fetchLock = new SemaphoreSlim(1, 1);

    public CdnFetcher(Uri url, HttpClient httpClient, ILogger? logger = null)
        : this(url, httpClient, new RetryStrategy(logger), logger)
    {
    }

    // visible for testing
    public CdnFetcher(Uri url, HttpClient httpClient, RetryStrategy retryStrategy, ILogger? logger = null)
        : this(url, httpClient, retryStrategy, RequestTimeout, logger)
    {
    }

    // visible for testing
    public CdnFetcher(
        Uri url, HttpClient httpClient, RetryStrategy retryStrategy, TimeSpan requestTimeout, ILogger? logger = null)
    {
        _url = url;
        _httpClient = httpClient;
        _retryStrategy = retryStrategy;
        _requestTimeout = requestTimeout;
        _logger = logger;
    }

    /// <summary>
    /// Promotes the validators staged by the last successful fetch.
    /// </summary>
    /// <remarks>
    /// Call this only once the fetched body has been applied. Until then the previous validators stay in force, so
    /// a body that is empty or fails to store is re-requested in full on the next fetch instead of being answered
    /// with a 304 ("what you have is current") for a config that was never actually loaded.
    /// </remarks>
    public void Commit()
    {
        var pending = _pendingState;
        if (pending is not null)
        {
            _state = pending;
            _pendingState = null;
        }
    }

    /// <summary>Returns <c>true</c> if a 429 Retry-After period is still active.</summary>
    public bool IsRateLimited => DateTime.UtcNow.Ticks < Interlocked.Read(ref _rateLimitedUntilTicks);

    /// <summary>
    /// Fetches the flag configuration from the CDN with retry.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="FetchResult.Skipped"/> without making a request when a fetch is already in flight
    /// (KB section 2.1.1) or when inside an active Retry-After window (KB section 2.3). The in-flight guard is what
    /// keeps the cache state consistent: a conditional request reads the validators before sending and stores the
    /// new pair after, and interleaving two fetches could store an ETag and a Last-Modified that came from
    /// different responses.
    /// </remarks>
    /// <returns>A <see cref="FetchResult"/>; the body is null on 304, 429, a skip, or a stale-node response.</returns>
    /// <exception cref="HttpRequestException">Network failure after all retry attempts were exhausted.</exception>
    /// <exception cref="TimeoutException">Every attempt exceeded the request timeout.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<FetchResult> FetchAsync(CancellationToken cancellationToken = default)
    {
        if (IsRateLimited)
        {
            _logger?.LogDebug("inside Retry-After window; skipping fetch (no request made)");
            return FetchResult.Skipped;
        }

        if (!await _fetchLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger?.LogDebug("fetch already in progress; skipping (no request made)");
            return FetchResult.Skipped;
        }

        try
        {
            return await _retryStrategy.ExecuteAsync(DoFetchAsync, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    private async Task<FetchResult> DoFetchAsync(CancellationToken cancellationToken)
    {
        var current = _state;
        _pendingState = null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _url);
            if (current.ETag is not null)
            {
                // echoed verbatim, as the other providers do; the typed header rejects unquoted tags
                request.Headers.TryAddWithoutValidation("If-None-Match", current.ETag);
            }

            if (current.LastModifiedAt is not null)
            {
                request.Headers.IfModifiedSince = current.LastModifiedAt;
            }

            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"CDN request timed out after {_requestTimeout.TotalSeconds:0.###}s");
        }

        using (response)
        {
            var statusCode = (int)response.StatusCode;

            if (statusCode == 304)
            {
                return new FetchResult(304, null, current.ETag, current.LastModifiedAt);
            }

            if (statusCode == 429)
            {
                var retryAfter = ParseRetryAfter(response.Headers.RetryAfter);
                Interlocked.Exchange(ref _rateLimitedUntilTicks, (DateTime.UtcNow + retryAfter).Ticks);
                _logger?.LogWarning("rate limited (429); pausing fetches for {RetryAfter}", retryAfter);
                return new FetchResult(429, null, null, null);
            }

            var newETag = response.Headers.TryGetValues("ETag", out var eTags) ? eTags.FirstOrDefault() : null;
            var newLastModifiedAt = response.Content.Headers.LastModified;
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (statusCode >= 200 && statusCode < 300)
            {
                // stale CDN node defense: a 200 with no newer Last-Modified is a 304; missing header skips it
                if (newLastModifiedAt is not null
                    && current.LastModifiedAt is not null
                    && newLastModifiedAt <= current.LastModifiedAt)
                {
                    _logger?.LogWarning(
                        "CDN returned 200 but Last-Modified ({NewLastModified}) is not later than stored ({StoredLastModified}); "
                        + "treating as 304 (suspected stale node)",
                        newLastModifiedAt,
                        current.LastModifiedAt);
                    return new FetchResult(304, null, current.ETag, current.LastModifiedAt);
                }

                // stage only what this response gave us; never mix tokens across requests
                _pendingState = new CacheState(newETag, newLastModifiedAt);
            }

            return new FetchResult(statusCode, body, newETag, newLastModifiedAt);
        }
    }

    /// <summary>
    /// Reads a Retry-After header, which is either a number of seconds or an HTTP-date. Falls back to
    /// <see cref="DefaultRetryAfter"/> when it is missing or unparseable.
    /// </summary>
    internal static TimeSpan ParseRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return DefaultRetryAfter;
    }

    private sealed class CacheState
    {
        internal static readonly CacheState Empty = new CacheState(null, null);

        internal CacheState(string? eTag, DateTimeOffset? lastModifiedAt)
        {
            ETag = eTag;
            LastModifiedAt = lastModifiedAt;
        }

        internal string? ETag { get; }

        internal DateTimeOffset? LastModifiedAt { get; }
    }
}
