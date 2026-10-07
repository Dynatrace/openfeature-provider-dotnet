using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Model;
using OpenFeature.Providers.Flagd.Core;

namespace Dynatrace.OpenFeature.Tests;

/// <summary>
/// The provider's evaluation metrics wiring: the hook it exposes, how the version tracks the loaded config, and when the
/// aggregate reaches the metrics endpoint.
/// </summary>
public class DynatraceProviderMetricsTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    // no-sleep retry strategy for fast tests
    private static readonly RetryStrategy NoDelayRetry = new RetryStrategy(new Random(42), (_, _) => Task.CompletedTask);

    private readonly MockHttpServer _cdnServer = new MockHttpServer();
    private readonly MockHttpServer _metricsServer = new MockHttpServer();

    private DynatraceProvider Provider(DynatraceProviderOptions? options = null)
    {
        var safeOptions = options ?? new DynatraceProviderOptions { MetricsFlushInterval = TimeSpan.FromHours(1) };
        var fetcher = new CdnFetcher(DynatraceProvider.BuildCdnUrl(TestFixtures.SdkKey, safeOptions), _cdnServer.CreateClient(), NoDelayRetry);
        return new DynatraceProvider(
            TestFixtures.SdkKey,
            safeOptions,
            new FlagdCore(),
            fetcher,
            _metricsServer.CreateClient(),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMinutes(1));
    }

    private static DynatraceMetricsHook HookOf(DynatraceProvider provider) => (DynatraceMetricsHook)provider.GetProviderHooks().Single();

    private static FlagEvaluationDetails<object> StaticDetails(string version)
        => new FlagEvaluationDetails<object>(
            "my-bool-flag",
            true,
            ErrorType.None,
            Reason.Static,
            "on",
            null,
            new ImmutableMetadata(new Dictionary<string, object> { ["version"] = version }));

    private static ValueTask Evaluate(DynatraceMetricsHook hook, FlagEvaluationDetails<object> details)
        => hook.FinallyAsync(
            new HookContext<object>("my-bool-flag", false, FlagValueType.Boolean, new ClientMetadata("test-client", "1.0"), new Metadata("dynatrace"), EvaluationContext.Empty),
            details,
            new Dictionary<string, object>());

    [Fact]
    public void ExposesTheDynatraceMetricsHook()
    {
        var provider = Provider();

        var hooks = provider.GetProviderHooks();

        Assert.Single(hooks);
        Assert.IsType<DynatraceMetricsHook>(hooks[0]);
    }

    [Fact]
    public async Task Shutdown_FlushesAggregatedMetricsToTheMetricsEndpoint()
    {
        _cdnServer.Enqueue(MockResponse.Status(200).Body(TestFixtures.FlagConfig));
        _metricsServer.Enqueue(MockResponse.Status(202));
        var provider = Provider();
        await provider.InitializeAsync(EvaluationContext.Empty);
        await Evaluate(HookOf(provider), StaticDetails("1741780800"));

        await provider.ShutdownAsync();

        var request = await _metricsServer.TakeRequestAsync(Wait);
        Assert.NotNull(request);
        Assert.Equal(HttpMethod.Post, request!.Method);
        Assert.Equal("https://metrics.us.fm.dynatrace.com/v1/metrics", request.Url.ToString());
        Assert.Equal("application/json", request.GetHeader("Content-Type"));
        Assert.Equal($"Bearer {TestFixtures.SdkKey}", request.GetHeader("Authorization"));
        Assert.Contains("\"providerName\":\"dynatrace\"", request.Body);
        Assert.Contains("\"flagKey\":\"my-bool-flag\"", request.Body);
        Assert.Contains("\"variant\":\"on\"", request.Body);
        Assert.Contains("\"evaluationReason\":\"static\"", request.Body);
    }

    [Fact]
    public async Task Shutdown_ReportsThePackageVersionAsTheSdkVersion()
    {
        _cdnServer.Enqueue(MockResponse.Status(200).Body(TestFixtures.FlagConfig));
        _metricsServer.Enqueue(MockResponse.Status(202));
        var provider = Provider();
        await provider.InitializeAsync(EvaluationContext.Empty);
        await Evaluate(HookOf(provider), StaticDetails("1741780800"));

        await provider.ShutdownAsync();

        var request = await _metricsServer.TakeRequestAsync(Wait);
        using var document = System.Text.Json.JsonDocument.Parse(request!.Body);
        var sdkVersion = document.RootElement.GetProperty("sdkVersion").GetString();
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", sdkVersion); // a clean version, with no "+commit" suffix
    }

    [Fact]
    public async Task Shutdown_WithoutEvaluations_SendsNothing()
    {
        _cdnServer.Enqueue(MockResponse.Status(200).Body(TestFixtures.FlagConfig));
        var provider = Provider();
        await provider.InitializeAsync(EvaluationContext.Empty);

        await provider.ShutdownAsync();

        Assert.Null(await _metricsServer.TakeRequestAsync(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task Evaluations_BeforeTheConfigIsLoaded_AreNotRecorded()
    {
        _metricsServer.Enqueue(MockResponse.Status(202));
        var provider = Provider(); // never initialized: no config version observed yet

        await Evaluate(HookOf(provider), StaticDetails("1741780800"));
        await provider.ShutdownAsync();

        Assert.Null(await _metricsServer.TakeRequestAsync(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task StoreFlags_SealsTheBatchForTheCurrentVersionBeforeSwitching()
    {
        // Both POSTs are delivered by the shutdown flush: sealed v1 batch first, live v2 batch second.
        _metricsServer.Enqueue(MockResponse.Status(202));
        _metricsServer.Enqueue(MockResponse.Status(202));
        _cdnServer.Enqueue(MockResponse.Status(200).Body(TestFixtures.FlagConfig));
        var provider = Provider();
        await provider.InitializeAsync(EvaluationContext.Empty);
        var hook = HookOf(provider);

        // two evaluations against config v1
        await Evaluate(hook, StaticDetails("1741780800"));
        await Evaluate(hook, StaticDetails("1741780800"));

        // The CDN poller detects a new config, sealing the v1 batch and scheduling an async flush. StoreFlags must
        // return immediately without blocking.
        provider.StoreFlags(TestFixtures.FlagConfig.Replace("1741780800", "1741780900"));

        // one evaluation against config v2 goes to the fresh live batch
        await Evaluate(hook, StaticDetails("1741780900"));

        // Shutdown delivers the sealed v1 batch first, then the live v2 batch.
        await provider.ShutdownAsync();

        var v1 = await _metricsServer.TakeRequestAsync(Wait);
        Assert.NotNull(v1); // the v1 sealed batch
        Assert.Contains("\"version\":\"1741780800\"", v1!.Body);
        Assert.Contains("\"count\":2", v1.Body);

        var v2 = await _metricsServer.TakeRequestAsync(Wait);
        Assert.NotNull(v2); // the v2 live batch
        Assert.Contains("\"version\":\"1741780900\"", v2!.Body);
        Assert.DoesNotContain("1741780800", v2.Body); // the v1 version must not appear in the v2 batch
    }

    [Fact]
    public async Task StoreFlags_DropsEvaluationsStillTaggedWithTheOutgoingVersion()
    {
        _cdnServer.Enqueue(MockResponse.Status(200).Body(TestFixtures.FlagConfig));
        _metricsServer.Enqueue(MockResponse.Status(202));
        var provider = Provider();
        await provider.InitializeAsync(EvaluationContext.Empty);
        var hook = HookOf(provider);
        provider.StoreFlags(TestFixtures.FlagConfig.Replace("1741780800", "1741780900"));

        // an evaluation that began under v1 but finished after the switch
        await Evaluate(hook, StaticDetails("1741780800"));
        await provider.ShutdownAsync();

        Assert.Null(await _metricsServer.TakeRequestAsync(TimeSpan.FromMilliseconds(200)));
    }

    // -- metrics URL and options --

    [Fact]
    public void BuildMetricsUrl_DerivesFromTheKey()
    {
        var url = DynatraceProvider.BuildMetricsUrl("dt01.server_eu_abcdef1234.de848e97a9cc4cc78aae568e65f49a9d_a1b2c3d4e5", null);

        Assert.Equal("https://metrics.eu.fm.dynatrace.com/v1/metrics", url.ToString());
    }

    [Theory]
    [InlineData("https://metrics.example.com")]
    [InlineData("https://metrics.example.com/")]
    [InlineData("  https://metrics.example.com  ")]
    [InlineData("https://metrics.example.com:443")] // the default port is normalised away
    public void BuildMetricsUrl_MetricsOriginAppendsTheMetricsPath(string origin)
    {
        var url = DynatraceProvider.BuildMetricsUrl(TestFixtures.SdkKey, new DynatraceProviderOptions { MetricsOrigin = origin });

        Assert.Equal("https://metrics.example.com/v1/metrics", url.ToString());
    }

    [Fact]
    public void BuildMetricsUrl_KeepsAnExplicitPort()
    {
        var url = DynatraceProvider.BuildMetricsUrl(TestFixtures.SdkKey, new DynatraceProviderOptions { MetricsOrigin = "http://localhost:9090" });

        Assert.Equal("http://localhost:9090/v1/metrics", url.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://metrics.example.com/v1/metrics")]
    [InlineData("https://metrics.example.com//")]
    [InlineData("https://metrics.example.com/..")]
    [InlineData("https://metrics.example.com?x=1")]
    [InlineData("ftp://metrics.example.com")]
    public void MetricsOrigin_InvalidOrigin_Throws(string origin)
    {
        var options = new DynatraceProviderOptions();

        Assert.Throws<ArgumentException>(() => options.MetricsOrigin = origin);
    }

    [Fact]
    public void MetricsOrigin_DefaultsToNullAndCanBeCleared()
    {
        var options = new DynatraceProviderOptions();
        Assert.Null(options.MetricsOrigin);

        options.MetricsOrigin = "https://metrics.example.com";
        options.MetricsOrigin = null;

        Assert.Null(options.MetricsOriginUri);
    }

    [Fact]
    public void MetricsFlushInterval_DefaultsTo30Seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), new DynatraceProviderOptions().MetricsFlushInterval);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ResolveMetricsFlushInterval_InvalidValue_FallsBackToTheDefaultWithAWarning(int seconds)
    {
        var logger = new ListLogger();

        var resolved = DynatraceProvider.ResolveMetricsFlushInterval(TimeSpan.FromSeconds(seconds), logger);

        Assert.Equal(TimeSpan.FromSeconds(30), resolved);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("MetricsFlushInterval"));
    }

    [Fact]
    public void ResolveMetricsFlushInterval_SubMillisecondValue_FallsBackToTheDefault()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            DynatraceProvider.ResolveMetricsFlushInterval(TimeSpan.FromTicks(1), null));
    }

    [Fact]
    public void ResolveMetricsFlushInterval_ValidValue_IsKept()
    {
        var logger = new ListLogger();

        var resolved = DynatraceProvider.ResolveMetricsFlushInterval(TimeSpan.FromMinutes(5), logger);

        Assert.Equal(TimeSpan.FromMinutes(5), resolved);
        Assert.Empty(logger.Entries);
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new List<(LogLevel, string)>();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}

/// <summary>
/// Drives metrics through the real OpenFeature API, to prove the SDK invokes the provider-owned hook for every client
/// evaluation. The API is a process-wide singleton, so these tests share the serialized collection.
/// </summary>
[Collection("OpenFeature API")]
public class DynatraceProviderMetricsSdkTests : IAsyncLifetime
{
    private const string Domain = "dynatrace-metrics-tests";

    private readonly MockHttpServer _cdnServer = new MockHttpServer();
    private readonly MockHttpServer _metricsServer = new MockHttpServer();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Api.Instance.ShutdownAsync();

    [Fact]
    public async Task ClientEvaluations_AreRecordedByTheProviderHookAndFlushedOnShutdown()
    {
        _cdnServer.Enqueue(MockResponse.Status(200).Body(TestFixtures.FlagConfig));
        _metricsServer.Enqueue(MockResponse.Status(202));
        var provider = new DynatraceProvider(
            TestFixtures.SdkKey,
            new DynatraceProviderOptions { MetricsFlushInterval = TimeSpan.FromMinutes(5) },
            new FlagdCore(),
            new CdnFetcher(
                DynatraceProvider.BuildCdnUrl(TestFixtures.SdkKey, null),
                _cdnServer.CreateClient(),
                new RetryStrategy(new Random(42), (_, _) => Task.CompletedTask)),
            _metricsServer.CreateClient(),
            DynatraceProvider.PollIntervalSseDisconnected,
            DynatraceProvider.GracePeriod);
        await Api.Instance.SetProviderAsync(Domain, provider);
        var client = Api.Instance.GetClient(Domain);

        Assert.True(await client.GetBooleanValueAsync("my-bool-flag", false));
        await client.GetBooleanValueAsync("no-such-flag", true); // an error evaluation is recorded too

        await Api.Instance.ShutdownAsync();

        var request = await _metricsServer.TakeRequestAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(request); // a metrics POST from the SDK-triggered provider hook
        Assert.Equal("/v1/metrics", request!.Path);
        Assert.Contains("\"flagKey\":\"my-bool-flag\"", request.Body);
        Assert.Contains("\"flagKey\":\"no-such-flag\"", request.Body);
        Assert.Contains("\"errorCode\":\"flag_not_found\"", request.Body);
    }
}
