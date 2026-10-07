using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Dynatrace.OpenFeature.Tests;

public class CdnFetcherTests
{
    private const string TestETag = "\"abc123\""; // per RFC 7232 ETags are quoted strings
    private const string HeaderETag = "ETag";
    private const string HeaderLastModified = "Last-Modified";
    private const string HeaderRetryAfter = "Retry-After";
    private const string HeaderIfNoneMatch = "If-None-Match";
    private const string HeaderIfModifiedSince = "If-Modified-Since";

    private const string LastModifiedOld = "Mon, 01 Jan 2024 00:00:00 GMT";
    private const string LastModifiedNew = "Wed, 02 Apr 2025 12:00:00 GMT";

    private static readonly DateTimeOffset LastModifiedOldAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly string MinimalFlagsBody = TestFixtures.MinimalFlags;

    // no-sleep retry strategy for fast tests
    private static readonly RetryStrategy NoDelayRetry = new RetryStrategy(new Random(42), (_, _) => Task.CompletedTask);

    private readonly MockHttpServer _server = new MockHttpServer();

    private CdnFetcher Fetcher() => new CdnFetcher(new Uri("https://cdn.example.com/flags.json"), _server.CreateClient(), NoDelayRetry);

    [Fact]
    public async Task SuccessfulFetch_ReturnsBodyETagAndLastModified()
    {
        _server.Enqueue(MockResponse.Status(200).Body(MinimalFlagsBody)
            .Header(HeaderETag, TestETag).Header(HeaderLastModified, LastModifiedOld));

        var result = await Fetcher().FetchAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(MinimalFlagsBody, result.Body);
        Assert.Equal(TestETag, result.ETag);
        Assert.Equal(LastModifiedOldAt, result.LastModified);
    }

    [Fact]
    public async Task Fetch_RequestsTheUrlWithGet()
    {
        _server.Enqueue(MockResponse.Status(200).Body("{}"));

        await Fetcher().FetchAsync();

        var request = Assert.Single(_server.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://cdn.example.com/flags.json", request.Url.ToString());
    }

    [Fact]
    public async Task SecondFetch_SendsConditionalHeaders()
    {
        _server.Enqueue(MockResponse.Status(200).Body(MinimalFlagsBody)
            .Header(HeaderETag, TestETag).Header(HeaderLastModified, LastModifiedOld));
        _server.Enqueue(MockResponse.Status(304));

        var fetcher = Fetcher();
        await fetcher.FetchAsync();
        await fetcher.FetchAsync();

        var second = _server.Requests[1];
        Assert.Equal(TestETag, second.GetHeader(HeaderIfNoneMatch));
        Assert.Equal(LastModifiedOld, second.GetHeader(HeaderIfModifiedSince));
    }

    [Fact]
    public async Task NotModifiedResponse_ReturnsNullBody()
    {
        _server.Enqueue(MockResponse.Status(200).Body(MinimalFlagsBody)
            .Header(HeaderETag, TestETag).Header(HeaderLastModified, LastModifiedOld));
        _server.Enqueue(MockResponse.Status(304));

        var fetcher = Fetcher();
        await fetcher.FetchAsync();
        var result = await fetcher.FetchAsync();

        Assert.True(result.IsNotModified);
        Assert.Null(result.Body);
    }

    [Fact]
    public async Task FirstFetch_DoesNotSendConditionalHeaders()
    {
        _server.Enqueue(MockResponse.Status(200).Body("{}"));

        await Fetcher().FetchAsync();

        var request = Assert.Single(_server.Requests);
        Assert.Null(request.GetHeader(HeaderIfNoneMatch));
        Assert.Null(request.GetHeader(HeaderIfModifiedSince));
    }

    [Fact]
    public async Task InvalidateCache_MakesTheNextFetchUnconditional()
    {
        _server.Enqueue(MockResponse.Status(200).Body(MinimalFlagsBody)
            .Header(HeaderETag, TestETag).Header(HeaderLastModified, LastModifiedOld));
        _server.Enqueue(MockResponse.Status(200).Body(MinimalFlagsBody));

        var fetcher = Fetcher();
        await fetcher.FetchAsync();
        fetcher.InvalidateCache();
        await fetcher.FetchAsync();

        var second = _server.Requests[1];
        Assert.Null(second.GetHeader(HeaderIfNoneMatch));
        Assert.Null(second.GetHeader(HeaderIfModifiedSince));
    }

    [Fact]
    public async Task ServerError_IsRetriedAndReturnsLastStatus()
    {
        for (var i = 0; i < RetryStrategy.MaxAttempts; i++)
        {
            _server.Enqueue(MockResponse.Status(503));
        }

        var result = await Fetcher().FetchAsync();

        Assert.Equal(503, result.StatusCode);
        Assert.False(result.IsSuccess);
        Assert.Equal(RetryStrategy.MaxAttempts, _server.RequestCount);
    }

    [Fact]
    public async Task ServerError_RecoversOnTheSingleRetry()
    {
        // KB 2.4: two attempts total, so recovery has to happen on the first (and only) retry
        _server.Enqueue(MockResponse.Status(503));
        _server.Enqueue(MockResponse.Status(200).Body(MinimalFlagsBody).Header(HeaderETag, TestETag));

        var result = await Fetcher().FetchAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(2, _server.RequestCount);
    }

    [Fact]
    public async Task NetworkFailure_IsRetriedThenPropagates()
    {
        for (var i = 0; i < RetryStrategy.MaxAttempts; i++)
        {
            _server.Enqueue(MockResponse.NetworkFailure("connection refused"));
        }

        await Assert.ThrowsAsync<HttpRequestException>(() => Fetcher().FetchAsync());

        Assert.Equal(RetryStrategy.MaxAttempts, _server.RequestCount);
    }

    [Fact]
    public async Task RequestExceedingTheTimeout_ThrowsTimeoutException()
    {
        _server.Dispatcher = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return MockResponse.Status(200);
        };
        var fetcher = new CdnFetcher(
            new Uri("https://cdn.example.com/flags.json"), _server.CreateClient(), NoDelayRetry, TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TimeoutException>(() => fetcher.FetchAsync());

        Assert.Equal(RetryStrategy.MaxAttempts, _server.RequestCount);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesWithoutRetry()
    {
        _server.Dispatcher = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return MockResponse.Status(200);
        };
        var fetcher = new CdnFetcher(
            new Uri("https://cdn.example.com/flags.json"), _server.CreateClient(), NoDelayRetry, TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetcher.FetchAsync(cts.Token));

        Assert.Equal(1, _server.RequestCount);
    }

    [Fact]
    public async Task TicksInsideTheRetryAfterWindow_AreSkippedWithoutNetworkCall()
    {
        // KB 2.3: the 429 ends the fetch and opens a rate-limit window; ticks inside that
        // window make no request, so they are skipped rather than reported as an outcome.
        _server.Enqueue(MockResponse.Status(429).Header(HeaderRetryAfter, "60"));

        var fetcher = Fetcher();
        var first = await fetcher.FetchAsync();
        Assert.Equal(429, first.StatusCode); // the response that opens the window is a real 429
        Assert.False(first.IsSkipped);
        Assert.True(fetcher.IsRateLimited);

        var suppressed = await fetcher.FetchAsync();
        Assert.True(suppressed.IsSkipped); // a tick inside the window should be skipped
        Assert.False(suppressed.IsSuccess);
        Assert.False(suppressed.IsNotModified); // a suppressed tick must not masquerade as a 304
        Assert.Equal(1, _server.RequestCount); // no request should be made inside the window
    }

    [Fact]
    public async Task FourTwentyNine_IsNotRetried()
    {
        _server.Enqueue(MockResponse.Status(429).Header(HeaderRetryAfter, "30"));

        await Fetcher().FetchAsync();

        Assert.Equal(1, _server.RequestCount);
    }

    [Fact]
    public async Task TooManyRequestsWithUnparseableRetryAfter_OpensTheDefaultWindow()
    {
        _server.Enqueue(MockResponse.Status(429).Header(HeaderRetryAfter, "not-a-number-or-date"));

        var fetcher = Fetcher();
        await fetcher.FetchAsync();

        Assert.True(fetcher.IsRateLimited);
    }

    [Fact]
    public void ParseRetryAfter_Seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), CdnFetcher.ParseRetryAfter(new RetryConditionHeaderValue(TimeSpan.FromSeconds(30))));
        Assert.Equal(TimeSpan.FromSeconds(120), CdnFetcher.ParseRetryAfter(new RetryConditionHeaderValue(TimeSpan.FromSeconds(120))));
    }

    [Fact]
    public void ParseRetryAfter_Missing_DefaultsTo60s()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), CdnFetcher.ParseRetryAfter(null));
    }

    [Fact]
    public void ParseRetryAfter_PastHttpDate_IsZero()
    {
        var past = new RetryConditionHeaderValue(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(TimeSpan.Zero, CdnFetcher.ParseRetryAfter(past));
    }

    [Fact]
    public void ParseRetryAfter_FutureHttpDate_IsTheRemainingWait()
    {
        var future = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(30));

        var wait = CdnFetcher.ParseRetryAfter(future);

        Assert.InRange(wait, TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task StaleNodeWithOlderLastModified_IsTreatedAsNotModified()
    {
        _server.Enqueue(MockResponse.Status(200).Body(MinimalFlagsBody)
            .Header(HeaderETag, TestETag).Header(HeaderLastModified, LastModifiedNew));
        _server.Enqueue(MockResponse.Status(200).Body("stale-body")
            .Header(HeaderETag, "\"stale-etag\"").Header(HeaderLastModified, LastModifiedOld));

        var fetcher = Fetcher();
        await fetcher.FetchAsync();
        var result = await fetcher.FetchAsync();

        Assert.True(result.IsNotModified);
        Assert.Null(result.Body);
    }

    [Fact]
    public async Task EqualLastModified_IsTreatedAsNotModified()
    {
        _server.Enqueue(MockResponse.Status(200).Body(MinimalFlagsBody)
            .Header(HeaderETag, TestETag).Header(HeaderLastModified, LastModifiedOld));
        _server.Enqueue(MockResponse.Status(200).Body("possibly-stale-body")
            .Header(HeaderETag, TestETag).Header(HeaderLastModified, LastModifiedOld));

        var fetcher = Fetcher();
        await fetcher.FetchAsync();
        var result = await fetcher.FetchAsync();

        Assert.True(result.IsNotModified);
        Assert.Null(result.Body);
    }

    [Fact]
    public async Task ResponseMissingLastModified_IsAccepted()
    {
        _server.Enqueue(MockResponse.Status(200).Body(MinimalFlagsBody)
            .Header(HeaderETag, TestETag).Header(HeaderLastModified, LastModifiedOld));
        _server.Enqueue(MockResponse.Status(200).Body("new-body").Header(HeaderETag, "\"new-etag\""));

        var fetcher = Fetcher();
        await fetcher.FetchAsync();
        var result = await fetcher.FetchAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("new-body", result.Body);
    }

    [Fact]
    public async Task ConcurrentFetch_IsSkippedNotReportedAs304()
    {
        // KB 2.1.1: a contended fetch must be distinguishable from a successful revalidation.
        // Reporting it as 304 makes it look like a successful poll and resets the grace timer.
        var firstInFlight = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.Dispatcher = async (_, _) =>
        {
            firstInFlight.TrySetResult(true);
            await release.Task;
            return MockResponse.Status(200).Body(MinimalFlagsBody);
        };

        var fetcher = Fetcher();
        var first = fetcher.FetchAsync();
        await firstInFlight.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var contended = await fetcher.FetchAsync();

        Assert.True(contended.IsSkipped); // a contended fetch should report as skipped
        Assert.False(contended.IsNotModified); // a skipped fetch must not masquerade as a 304
        Assert.False(contended.IsSuccess);
        Assert.False(contended.IsRateLimited);

        release.SetResult(true);
        Assert.True((await first).IsSuccess); // the in-flight fetch should complete normally
        Assert.Equal(1, _server.RequestCount); // the contended caller must not make a request
    }

    // -- decoding the configuration body --

    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

    [Fact]
    public async Task Body_IsDecodedAsUtf8_WhenTheResponseDeclaresNoCharset()
    {
        const string config = "{\"flags\":{\"grüß-gott\":{\"variants\":{\"☃\":\"ünïcödé\"}}}}";
        _server.Enqueue(MockResponse.Status(200).BodyBytes(Utf8.GetBytes(config)));

        var result = await Fetcher().FetchAsync();

        Assert.Equal(config, result.Body);
    }

    [Theory]
    [InlineData("application/json; charset=iso-8859-1")]
    [InlineData("application/json; charset=utf-16")]
    [InlineData("application/json; charset=\"not-a-real-charset\"")]
    public async Task Body_IsDecodedAsUtf8_EvenWhenTheResponseDeclaresAnotherCharset(string contentType)
    {
        // JSON is UTF-8 by definition (RFC 8259); a misconfigured CDN header must not change what the flags say.
        const string config = "{\"flags\":{\"f\":{\"variants\":{\"v\":\"ünïcödé ☃\"}}}}";
        _server.Enqueue(MockResponse.Status(200).BodyBytes(Utf8.GetBytes(config)).Header("Content-Type", contentType));

        var result = await Fetcher().FetchAsync();

        Assert.Equal(config, result.Body);
    }

    [Fact]
    public async Task Body_ByteOrderMarkIsStripped()
    {
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Utf8.GetBytes("{\"flags\":{}}")).ToArray();
        _server.Enqueue(MockResponse.Status(200).BodyBytes(withBom));

        var result = await Fetcher().FetchAsync();

        Assert.Equal("{\"flags\":{}}", result.Body); // a leading U+FEFF would make the document unparseable
    }

    [Fact]
    public async Task Body_InvalidUtf8_ThrowsFormatExceptionInsteadOfSilentlyCorruptingTheConfig()
    {
        var corrupt = Utf8.GetBytes("{\"flags\":{\"f\":{\"variants\":{\"v\":\"abc").Concat(new byte[] { 0xC3, 0x28 }).ToArray();
        _server.Enqueue(MockResponse.Status(200).BodyBytes(corrupt));

        await Assert.ThrowsAsync<FormatException>(() => Fetcher().FetchAsync());
    }

    [Fact]
    public async Task Body_InvalidUtf8_DoesNotCacheValidators()
    {
        // The validators of a body that could not be read must not stand in for it on the next fetch.
        _server.Enqueue(MockResponse.Status(200).BodyBytes(new byte[] { 0xC3, 0x28 }).Header(HeaderETag, TestETag).Header(HeaderLastModified, LastModifiedOld));
        _server.Enqueue(MockResponse.Status(200).Body(MinimalFlagsBody));
        var fetcher = Fetcher();

        await Assert.ThrowsAsync<FormatException>(() => fetcher.FetchAsync());
        await fetcher.FetchAsync();

        var second = _server.Requests[1];
        Assert.Null(second.GetHeader(HeaderIfNoneMatch));
        Assert.Null(second.GetHeader(HeaderIfModifiedSince));
    }

    [Fact]
    public async Task ErrorResponseBody_IsNeverDecoded()
    {
        // an error page in some other encoding is not a configuration, and must not turn a 503 into a decoding error
        for (var i = 0; i < RetryStrategy.MaxAttempts; i++)
        {
            _server.Enqueue(MockResponse.Status(503).BodyBytes(new byte[] { 0xFF, 0xFE, 0xC3, 0x28 }));
        }

        var result = await Fetcher().FetchAsync();

        Assert.Equal(503, result.StatusCode);
        Assert.Null(result.Body);
    }
}
