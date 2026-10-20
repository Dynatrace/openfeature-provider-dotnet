using System;
using System.Net.Http;
using System.Threading.Tasks;
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Error;
using OpenFeature.Model;
using OpenFeature.Providers.Flagd.Core;

namespace Dynatrace.OpenFeature.Tests;

public class DynatraceProviderTests
{
    // no-sleep retry strategy for fast tests
    private static readonly RetryStrategy NoDelayRetry = new RetryStrategy(new Random(42), (_, _) => Task.CompletedTask);

    private readonly MockHttpServer _server = new MockHttpServer();

    private DynatraceProvider Provider()
    {
        var options = new DynatraceProviderOptions { ConfigOrigin = "https://cdn.example.com" };
        var fetcher = new CdnFetcher(DynatraceProvider.BuildCdnUrl(TestFixtures.SdkKey, options), _server.CreateClient(), NoDelayRetry);
        return new DynatraceProvider(TestFixtures.SdkKey, options, new FlagdCore(), fetcher);
    }

    private async Task<DynatraceProvider> InitializedProviderAsync()
    {
        _server.Enqueue(MockResponse.Status(200).Body(TestFixtures.FlagConfig));
        var provider = Provider();
        await provider.InitializeAsync(EvaluationContext.Empty);
        return provider;
    }

    [Fact]
    public void ProviderName_IsDynatrace()
    {
        Assert.Equal("dynatrace", new DynatraceProvider(TestFixtures.SdkKey).GetMetadata().Name);
    }

    [Fact]
    public void NullOptions_IsTreatedAsNoOptions()
    {
        var provider = new DynatraceProvider(TestFixtures.SdkKey, null);

        Assert.Equal("dynatrace", provider.GetMetadata().Name);
    }

    // -- initialization --

    [Fact]
    public async Task Initialize_FetchesConfigAndResolvesFlagsCorrectly()
    {
        _server.Enqueue(MockResponse.Status(200).Body(TestFixtures.FlagConfig).Header("ETag", "\"v1\""));
        var provider = Provider();

        await provider.InitializeAsync(EvaluationContext.Empty);

        var boolEval = await provider.ResolveBooleanValueAsync("my-bool-flag", false, EvaluationContext.Empty);
        var strEval = await provider.ResolveStringValueAsync("my-string-flag", "default", EvaluationContext.Empty);
        Assert.True(boolEval.Value);
        Assert.Equal("world", strEval.Value);
        await provider.ShutdownAsync();
    }

    [Fact]
    public async Task Initialize_FetchesTheDerivedCdnUrl()
    {
        _server.Enqueue(MockResponse.Status(200).Body(TestFixtures.FlagConfig));
        var fetcher = new CdnFetcher(DynatraceProvider.BuildCdnUrl(TestFixtures.SdkKey, null), _server.CreateClient(), NoDelayRetry);
        var provider = new DynatraceProvider(TestFixtures.SdkKey, null, new FlagdCore(), fetcher);

        await provider.InitializeAsync(EvaluationContext.Empty);

        var request = Assert.Single(_server.Requests);
        Assert.Equal($"https://cdn.us.fm.dynatrace.com/server/{TestFixtures.SdkKey}.json", request.Url.ToString());
    }

    [Fact]
    public async Task Initialize_ThroughTheHttpClientConstructor_FetchesFromTheConfigOrigin()
    {
        _server.Enqueue(MockResponse.Status(200).Body(TestFixtures.FlagConfig));
        var options = new DynatraceProviderOptions { ConfigOrigin = "http://localhost:9999" };
        var provider = new DynatraceProvider(TestFixtures.SdkKey, options, new FlagdCore(), _server.CreateClient());

        await provider.InitializeAsync(EvaluationContext.Empty);

        var request = Assert.Single(_server.Requests);
        Assert.Equal($"http://localhost:9999/server/{TestFixtures.SdkKey}.json", request.Url.ToString());
    }

    [Fact]
    public async Task Initialize_HttpError_ThrowsHttpRequestException()
    {
        for (var i = 0; i < 5; i++)
        {
            _server.Enqueue(MockResponse.Status(503));
        }

        var provider = Provider();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => provider.InitializeAsync(EvaluationContext.Empty));
        Assert.Contains("503", ex.Message);
        await provider.ShutdownAsync();
    }

    [Fact]
    public async Task Initialize_NetworkFailure_ThrowsHttpRequestException()
    {
        for (var i = 0; i < RetryStrategy.MaxAttempts; i++)
        {
            _server.Enqueue(MockResponse.NetworkFailure("connection refused"));
        }

        var provider = Provider();

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.InitializeAsync(EvaluationContext.Empty));
    }

    [Fact]
    public async Task Initialize_EmptyBody_ThrowsParseError()
    {
        _server.Enqueue(MockResponse.Status(200).Body(string.Empty));
        var provider = Provider();

        await Assert.ThrowsAsync<ParseErrorException>(() => provider.InitializeAsync(EvaluationContext.Empty));
        await provider.ShutdownAsync();
    }

    [Fact]
    public async Task Initialize_InvalidJson_ThrowsParseError()
    {
        _server.Enqueue(MockResponse.Status(200).Body("not-json"));
        var provider = Provider();

        await Assert.ThrowsAsync<ParseErrorException>(() => provider.InitializeAsync(EvaluationContext.Empty));
        await provider.ShutdownAsync();
    }

    [Fact]
    public async Task Initialize_InitialFetchGivesUpAfterTheBoundedAttemptCount()
    {
        // KB 2.4: the 2-attempt cap applies to the initial fetch too
        for (var i = 0; i < RetryStrategy.MaxAttempts; i++)
        {
            _server.Enqueue(MockResponse.Status(503));
        }

        var provider = Provider();

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.InitializeAsync(EvaluationContext.Empty));
        Assert.Equal(RetryStrategy.MaxAttempts, _server.RequestCount);
    }

    [Fact]
    public async Task Initialize_RateLimitedFetch_ThrowsHttpRequestException()
    {
        _server.Enqueue(MockResponse.Status(429).Header("Retry-After", "30"));
        var provider = Provider();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => provider.InitializeAsync(EvaluationContext.Empty));

        Assert.Contains("429", ex.Message);
    }

    [Fact]
    public async Task Initialize_InvalidInitialConfig_DoesNotLeaveValidatorsThatTurnTheNextFetchInto304()
    {
        // rejected initial body; its ETag must not be sent, or the next fetch would 304
        const string badETag = "\"bad-config\"";
        _server.Dispatcher = (request, _) => Task.FromResult(
            request.GetHeader("If-None-Match") == badETag
                ? MockResponse.Status(304)
                : MockResponse.Status(200).Header("ETag", badETag).Body("{\"flags\": not-valid-json}"));
        var fetcher = new CdnFetcher(DynatraceProvider.BuildCdnUrl(TestFixtures.SdkKey, null), _server.CreateClient(), NoDelayRetry);
        var provider = new DynatraceProvider(TestFixtures.SdkKey, null, new FlagdCore(), fetcher);
        await Assert.ThrowsAsync<ParseErrorException>(() => provider.InitializeAsync(EvaluationContext.Empty));

        var next = await fetcher.FetchAsync();

        Assert.False(next.IsNotModified);
        Assert.Null(_server.Requests[1].GetHeader("If-None-Match"));
    }

    // -- evaluation --

    [Fact]
    public async Task Resolve_AllFlagTypes_ReturnConfiguredValues()
    {
        var provider = await InitializedProviderAsync();

        var boolean = await provider.ResolveBooleanValueAsync("my-bool-flag", false);
        var str = await provider.ResolveStringValueAsync("my-string-flag", "default");
        var integer = await provider.ResolveIntegerValueAsync("my-int-flag", 0);
        var dbl = await provider.ResolveDoubleValueAsync("my-double-flag", 0.0);
        var structure = await provider.ResolveStructureValueAsync("my-object-flag", new Value());

        Assert.True(boolean.Value);
        Assert.Equal("on", boolean.Variant);
        Assert.Equal("world", str.Value);
        Assert.Equal(2, integer.Value);
        Assert.Equal(3.14, dbl.Value);
        Assert.Equal(3, structure.Value.AsStructure!["retries"].AsInteger);
        Assert.Equal("x", structure.Value.AsStructure["name"].AsString);
    }

    [Fact]
    public async Task Resolve_UsesEvaluationContextForTargeting()
    {
        var provider = await InitializedProviderAsync();
        var targeted = EvaluationContext.Builder().Set("email", "test@dynatrace.com").Build();
        var other = EvaluationContext.Builder().Set("email", "someone@else.com").Build();

        var hit = await provider.ResolveBooleanValueAsync("my-targeted-flag", false, targeted);
        var miss = await provider.ResolveBooleanValueAsync("my-targeted-flag", true, other);

        Assert.True(hit.Value);
        Assert.Equal(Reason.TargetingMatch, hit.Reason);
        Assert.False(miss.Value);
    }

    [Fact]
    public async Task Resolve_UnknownFlag_ReportsFlagNotFound()
    {
        var provider = await InitializedProviderAsync();

        var ex = await Assert.ThrowsAsync<FeatureProviderException>(
            () => provider.ResolveBooleanValueAsync("no-such-flag", false, EvaluationContext.Empty));

        Assert.Equal(ErrorType.FlagNotFound, ex.ErrorType);
    }

    [Fact]
    public async Task Resolve_BeforeInitialize_ReportsFlagNotFound()
    {
        var provider = Provider();

        var ex = await Assert.ThrowsAsync<FeatureProviderException>(
            () => provider.ResolveBooleanValueAsync("any-flag", false, EvaluationContext.Empty));

        Assert.Equal(ErrorType.FlagNotFound, ex.ErrorType);
    }

    [Fact]
    public async Task Resolve_MakesNoNetworkCallAfterInitialization()
    {
        var provider = await InitializedProviderAsync();

        await provider.ResolveBooleanValueAsync("my-bool-flag", false);
        await provider.ResolveStringValueAsync("my-string-flag", "d");

        Assert.Equal(1, _server.RequestCount);
    }

    // -- shutdown --

    [Fact]
    public async Task Shutdown_IsIdempotent()
    {
        var provider = await InitializedProviderAsync();

        await provider.ShutdownAsync();
        await provider.ShutdownAsync();
        await provider.ShutdownAsync();
    }

    [Fact]
    public async Task Shutdown_BeforeInitialize_IsSafe()
    {
        await Provider().ShutdownAsync();
    }

    // -- SDK key and CDN URL --

    [Fact]
    public void BuildCdnUrl_DerivesFromKey()
    {
        var url = DynatraceProvider.BuildCdnUrl(TestFixtures.SdkKey, null);

        Assert.Equal($"https://cdn.us.fm.dynatrace.com/server/{TestFixtures.SdkKey}.json", url.ToString());
    }

    [Theory]
    [InlineData("eu")]
    [InlineData("de")]
    public void BuildCdnUrl_UsesGeoFromKey(string geo)
    {
        var url = DynatraceProvider.BuildCdnUrl($"dt01.server_{geo}_abcdef1234.de848e97a9cc4cc78aae568e65f49a9d_a1b2c3d4e5", null);

        Assert.Equal(geo, url.Host.Split('.')[1]);
        Assert.StartsWith($"https://cdn.{geo}.fm.dynatrace.com/server/", url.ToString());
    }

    [Fact]
    public void BuildCdnUrl_TrimsWhitespaceFromKey()
    {
        var url = DynatraceProvider.BuildCdnUrl($"  {TestFixtures.SdkKey}  ", null);

        Assert.EndsWith($"/{TestFixtures.SdkKey}.json", url.ToString());
    }

    [Theory]
    [InlineData("https://cdn.example.com/")]
    [InlineData("https://cdn.example.com")]
    public void BuildCdnUrl_ConfigOriginTrailingSlashIsNormalised(string origin)
    {
        var url = DynatraceProvider.BuildCdnUrl(TestFixtures.SdkKey, new DynatraceProviderOptions { ConfigOrigin = origin });

        Assert.Equal($"https://cdn.example.com/server/{TestFixtures.SdkKey}.json", url.ToString());
    }

    [Fact]
    public void BuildCdnUrl_ConfigOriginAcceptsUnderscoreHost()
    {
        var url = DynatraceProvider.BuildCdnUrl(TestFixtures.SdkKey, new DynatraceProviderOptions { ConfigOrigin = "http://my_service:8080" });

        Assert.Equal($"http://my_service:8080/server/{TestFixtures.SdkKey}.json", url.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsBlankKey(string? sdkKey)
    {
        Assert.Throws<ArgumentException>(() => new DynatraceProvider(sdkKey!));
    }

    [Theory]
    [InlineData("notavalidkey")] // completely invalid
    [InlineData("dt_server_us_03ee0ef7cd4244bfb8b71fea8f783f53")] // old format is no longer valid
    [InlineData("dt01.server_us_abcdef1234")] // missing private segment
    [InlineData("dt01.server_us_abc.de848e97a9cc4cc78aae568e65f49a9d_a1b2c3d4e5")] // publicRandom too short
    public void Constructor_RejectsInvalidKeyFormat(string sdkKey)
    {
        Assert.Throws<ArgumentException>(() => new DynatraceProvider(sdkKey));
    }

    [Theory]
    [InlineData("dt01.web_us_abcdef1234.de848e97a9cc4cc78aae568e65f49a9d_a1b2c3d4e5")]
    [InlineData("dt01.mobile_us_abcdef1234.de848e97a9cc4cc78aae568e65f49a9d_a1b2c3d4e5")]
    public void Constructor_RejectsNonServerType(string sdkKey)
    {
        Assert.Throws<ArgumentException>(() => new DynatraceProvider(sdkKey));
    }

    [Theory]
    [InlineData("dt01.server_us-east_abcdef1234.de848e97a9cc4cc78aae568e65f49a9d_a1b2c3d4e5")] // hyphen in geo
    [InlineData("dt01.server_US_abcdef1234.de848e97a9cc4cc78aae568e65f49a9d_a1b2c3d4e5")] // uppercase geo
    [InlineData("dt01.server_u_abcdef1234.de848e97a9cc4cc78aae568e65f49a9d_a1b2c3d4e5")] // single-char geo
    public void Constructor_RejectsInvalidGeo(string sdkKey)
    {
        Assert.Throws<ArgumentException>(() => new DynatraceProvider(sdkKey));
    }

    [Theory]
    [InlineData("dt01.server_us_abcdef1234.abc123_a1b2c3d4e5")] // secret too short
    [InlineData("dt01.server_us_abcdef1234.de848e97a9cc4cc78aae568e65f49a9d")] // missing sha1
    [InlineData("dt01.server_us_abcdef1234.de848e97a9cc4cc78aae568e65f49a9d_abc12")] // sha1 too short
    public void Constructor_RejectsInvalidPrivateSegment(string sdkKey)
    {
        Assert.Throws<ArgumentException>(() => new DynatraceProvider(sdkKey));
    }

    [Fact]
    public void Constructor_MalformedKey_DoesNotEchoTheSecret()
    {
        const string secret = "de848e97a9cc4cc78aae568e65f49a9d_a1b2c3d4e5X";

        var ex = Assert.Throws<ArgumentException>(() => new DynatraceProvider($"dt01.server_us_abcdef1234.{secret}"));

        Assert.DoesNotContain(secret, ex.Message);
        Assert.Contains("dt01.server_us_abcdef1234", ex.Message);
    }
}

/// <summary>
/// Drives the provider through the real OpenFeature API. The API is a process-wide singleton, so these
/// tests are serialized and always shut it down.
/// </summary>
[Collection("OpenFeature API")]
public class DynatraceProviderSdkTests : IAsyncLifetime
{
    private const string Domain = "dynatrace-tests";

    private readonly MockHttpServer _server = new MockHttpServer();
    private readonly MockHttpServer _metricsServer = new MockHttpServer();

    public Task InitializeAsync()
    {
        _metricsServer.Dispatcher = (_, _) => Task.FromResult(MockResponse.Status(202));
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Api.Instance.ShutdownAsync();

    private DynatraceProvider Provider() => new DynatraceProvider(
        TestFixtures.SdkKey,
        null,
        new FlagdCore(),
        new CdnFetcher(
            DynatraceProvider.BuildCdnUrl(TestFixtures.SdkKey, null),
            _server.CreateClient(),
            new RetryStrategy(new Random(42), (_, _) => Task.CompletedTask)),
        _metricsServer.CreateClient(),
        DynatraceProvider.PollIntervalSseDisconnected,
        DynatraceProvider.GracePeriod);

    [Fact]
    public async Task Client_EvaluatesFlagsFromCdnConfig()
    {
        _server.Enqueue(MockResponse.Status(200).Body(TestFixtures.FlagConfig));
        await Api.Instance.SetProviderAsync(Domain, Provider());
        var client = Api.Instance.GetClient(Domain);

        var enabled = await client.GetBooleanDetailsAsync("my-bool-flag", false);
        var missing = await client.GetBooleanDetailsAsync("no-such-flag", true);

        Assert.True(enabled.Value);
        Assert.Equal(Reason.Static, enabled.Reason);
        Assert.True(missing.Value); // falls back to the caller's default
        Assert.Equal(ErrorType.FlagNotFound, missing.ErrorType);
    }

    [Fact]
    public async Task Client_FailedInitialization_FallsBackToDefaults()
    {
        _server.Enqueue(MockResponse.Status(500));
        _server.Enqueue(MockResponse.Status(500));
        // The SDK absorbs a failed initialization (provider enters the error state) rather than throwing.
        await Api.Instance.SetProviderAsync(Domain, Provider());
        var client = Api.Instance.GetClient(Domain);

        var result = await client.GetBooleanDetailsAsync("my-bool-flag", true);

        Assert.True(result.Value);
        Assert.NotEqual(ErrorType.None, result.ErrorType);
    }
}
