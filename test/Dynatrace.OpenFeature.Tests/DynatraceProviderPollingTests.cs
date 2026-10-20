using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Error;
using OpenFeature.Model;
using OpenFeature.Providers.Flagd.Core;

namespace Dynatrace.OpenFeature.Tests;

/// <summary>
/// The provider's refresh behaviour: polling, the Stale/Error/Ready transitions it drives, and the events that
/// announce them.
/// </summary>
public class DynatraceProviderPollingTests
{
    // no-sleep retry strategy for fast tests
    private static readonly RetryStrategy NoDelayRetry = new RetryStrategy(new Random(42), (_, _) => Task.CompletedTask);

    private readonly MockHttpServer _server = new MockHttpServer();

    private DynatraceProvider Provider(TimeSpan pollInterval, TimeSpan gracePeriod)
    {
        var fetcher = new CdnFetcher(DynatraceProvider.BuildCdnUrl(TestFixtures.SdkKey, null), _server.CreateClient(), NoDelayRetry);
        return new DynatraceProvider(TestFixtures.SdkKey, null, new FlagdCore(), fetcher, pollInterval, gracePeriod);
    }

    /// <summary>Provider with a short poll interval and grace period for state management tests.</summary>
    private DynatraceProvider FastProvider(TimeSpan? gracePeriod = null)
        => Provider(TimeSpan.FromMilliseconds(50), gracePeriod ?? TimeSpan.FromMilliseconds(200));

    private static async Task AwaitStateAsync(DynatraceProvider provider, ProviderStatus expected, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (provider.State == expected)
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Equal(expected, provider.State);
    }

    private static async Task<ProviderEventPayload> ReadEventAsync(DynatraceProvider provider)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return (ProviderEventPayload)await provider.GetEventChannel().Reader.ReadAsync(cts.Token);
    }

    // -- state --

    [Fact]
    public void State_IsNotReadyBeforeInitialize()
    {
        Assert.Equal(ProviderStatus.NotReady, Provider(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)).State);
    }

    [Fact]
    public async Task State_IsReadyAfterInitialize()
    {
        _server.Enqueue(MockResponse.Status(200).Body(TestFixtures.FlagConfig));
        var provider = Provider(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

        await provider.InitializeAsync(EvaluationContext.Empty);

        Assert.Equal(ProviderStatus.Ready, provider.State);
        await provider.ShutdownAsync();
    }

    [Fact]
    public async Task State_AfterShutdown_IsNotReady()
    {
        _server.Enqueue(MockResponse.Status(200).Body(TestFixtures.FlagConfig));
        var provider = Provider(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        await provider.InitializeAsync(EvaluationContext.Empty);
        Assert.Equal(ProviderStatus.Ready, provider.State);

        await provider.ShutdownAsync();

        Assert.Equal(ProviderStatus.NotReady, provider.State);
    }

    [Fact]
    public async Task PollFailure_TransitionsToStale()
    {
        _server.Enqueue(MockResponse.Status(200).Body(TestFixtures.FlagConfig));
        for (var i = 0; i < RetryStrategy.MaxAttempts; i++)
        {
            _server.Enqueue(MockResponse.Status(503));
        }

        var provider = FastProvider(TimeSpan.FromSeconds(10));
        await provider.InitializeAsync(EvaluationContext.Empty);

        await AwaitStateAsync(provider, ProviderStatus.Stale, 3_000);
        await provider.ShutdownAsync();
    }

    [Fact]
    public async Task GracePeriodExpiry_TransitionsToError()
    {
        _server.Enqueue(MockResponse.Status(200).Body(TestFixtures.FlagConfig));
        for (var i = 0; i < RetryStrategy.MaxAttempts * 20; i++)
        {
            _server.Enqueue(MockResponse.Status(503));
        }

        var provider = FastProvider();
        await provider.InitializeAsync(EvaluationContext.Empty);

        await AwaitStateAsync(provider, ProviderStatus.Error, 3_000);
        await provider.ShutdownAsync();
    }

    [Fact]
    public async Task Recovery_FromStaleToReady()
    {
        _server.Enqueue(MockResponse.Status(200).Body(TestFixtures.FlagConfig));
        for (var i = 0; i < RetryStrategy.MaxAttempts; i++)
        {
            _server.Enqueue(MockResponse.Status(503));
        }

        var provider = FastProvider(TimeSpan.FromSeconds(10));
        await provider.InitializeAsync(EvaluationContext.Empty);
        await AwaitStateAsync(provider, ProviderStatus.Stale, 3_000);

        _server.Enqueue(MockResponse.Status(304)); // one 304 is enough to recover
        await AwaitStateAsync(provider, ProviderStatus.Ready, 3_000);
        await provider.ShutdownAsync();
    }

    [Fact]
    public async Task Recovery_FromError_ToReady()
    {
        // fails after the initial load until we flip the switch, then answers 304
        var healthy = false;
        var requests = 0;
        _server.Dispatcher = (_, _) =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                return Task.FromResult(MockResponse.Status(200).Body(TestFixtures.FlagConfig));
            }

            return Task.FromResult(Volatile.Read(ref healthy) ? MockResponse.Status(304) : MockResponse.Status(503));
        };
        var provider = FastProvider();
        await provider.InitializeAsync(EvaluationContext.Empty);
        await AwaitStateAsync(provider, ProviderStatus.Error, 3_000);

        Volatile.Write(ref healthy, true);

        await AwaitStateAsync(provider, ProviderStatus.Ready, 3_000);
        await provider.ShutdownAsync();
    }

    [Fact]
    public async Task EmptyBodyPoll_DoesNotRevalidateAgainstItsValidators()
    {
        // initial load, then an empty 200; afterwards a 304 only if asked with the empty body's ETag
        var sentRejectedETag = false;
        var requests = 0;
        _server.Dispatcher = (request, _) =>
        {
            var n = Interlocked.Increment(ref requests);
            if (n == 1)
            {
                return Task.FromResult(MockResponse.Status(200).Header("ETag", "\"v1\"").Body(TestFixtures.FlagConfig));
            }

            if (n == 2)
            {
                return Task.FromResult(MockResponse.Status(200).Header("ETag", "\"v2\"").Body(string.Empty));
            }

            if (request.GetHeader("If-None-Match") == "\"v2\"")
            {
                Volatile.Write(ref sentRejectedETag, true);
            }

            return Task.FromResult(MockResponse.Status(304));
        };
        var provider = FastProvider(TimeSpan.FromSeconds(10));
        await provider.InitializeAsync(EvaluationContext.Empty);
        await AwaitStateAsync(provider, ProviderStatus.Stale, 3_000);

        // recovers by revalidating against the applied v1, never the rejected v2
        await AwaitStateAsync(provider, ProviderStatus.Ready, 3_000);
        await provider.ShutdownAsync();
        Assert.False(Volatile.Read(ref sentRejectedETag));
    }

    // -- failed initialization --

    [Fact]
    public async Task FailedInit_StillStartsPollingAndRecovers()
    {
        // KB 2.4: a failed init recovers via polling instead of sticking in Error
        var requests = 0;
        _server.Dispatcher = (_, _) =>
        {
            // fail the whole initial fetch (both attempts), then serve a good config
            return Task.FromResult(Interlocked.Increment(ref requests) <= RetryStrategy.MaxAttempts
                ? MockResponse.Status(503)
                : MockResponse.Status(200).Body(TestFixtures.FlagConfig));
        };
        var provider = FastProvider(TimeSpan.FromSeconds(10));

        // a failed initial fetch should still surface to the caller
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.InitializeAsync(EvaluationContext.Empty));
        Assert.Equal(ProviderStatus.Error, provider.State);

        await AwaitStateAsync(provider, ProviderStatus.Ready, 5_000);
        var flag = await provider.ResolveBooleanValueAsync("my-bool-flag", false);
        Assert.True(flag.Value); // the recovering poll loaded the config
        await provider.ShutdownAsync();
    }

    [Fact]
    public async Task InvalidInitialConfig_DoesNotRecoverToReadyOnA304()
    {
        // rejected initial body; a 304 against its ETag would falsely recover to Ready
        const string badETag = "\"bad-config\"";
        _server.Dispatcher = (request, _) => Task.FromResult(
            request.GetHeader("If-None-Match") == badETag
                ? MockResponse.Status(304)
                : MockResponse.Status(200).Header("ETag", badETag).Body("{\"flags\": not-valid-json}"));
        var provider = FastProvider(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<ParseErrorException>(() => provider.InitializeAsync(EvaluationContext.Empty));

        await Task.Delay(400);
        // a 304 for a config that was never applied must not report Ready
        Assert.NotEqual(ProviderStatus.Ready, provider.State);
        await provider.ShutdownAsync();
    }

    [Fact]
    public async Task CancelledInit_DoesNotStartPolling()
    {
        _server.Dispatcher = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return MockResponse.Status(200);
        };
        var provider = FastProvider();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.InitializeAsync(EvaluationContext.Empty, cts.Token));
        var requestsAfterCancel = _server.RequestCount;
        await Task.Delay(300);

        Assert.Equal(requestsAfterCancel, _server.RequestCount);
        Assert.Equal(ProviderStatus.NotReady, provider.State);
    }

    // -- single-flight --

    [Fact]
    public async Task SkippedPollTicks_DoNotResetTheGraceTimer()
    {
        // KB 2.1.1: skipped ticks emit nothing; failures are delayed past the cadence so ticks contend
        var requests = 0;
        _server.Dispatcher = async (_, ct) =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                return MockResponse.Status(200).Body(TestFixtures.FlagConfig);
            }

            await Task.Delay(120, ct);
            return MockResponse.Status(503);
        };
        var provider = FastProvider();
        await provider.InitializeAsync(EvaluationContext.Empty);

        // first failure goes Stale; skipped ticks must not cancel the grace timer
        await AwaitStateAsync(provider, ProviderStatus.Error, 5_000);
        await provider.ShutdownAsync();
    }

    [Fact]
    public async Task ContendedTicks_DoNotPileUpRequests()
    {
        // single-flight: request count tracks fetch duration, not tick count
        var requests = 0;
        _server.Dispatcher = async (_, ct) =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                return MockResponse.Status(200).Body(TestFixtures.FlagConfig);
            }

            await Task.Delay(150, ct);
            return MockResponse.Status(304);
        };
        var provider = FastProvider(TimeSpan.FromSeconds(10));
        await provider.InitializeAsync(EvaluationContext.Empty);

        await Task.Delay(600);
        await provider.ShutdownAsync();

        // ~12 ticks fit in 600ms at a 50ms cadence; at 150ms per fetch only ~4 can actually run.
        var actual = Volatile.Read(ref requests);
        Assert.True(actual <= 7, $"expected contended ticks to be skipped, but {actual} requests were made");
    }

    // -- configuration changes --

    [Fact]
    public async Task ConfigurationChange_IsLoadedAndAnnounced()
    {
        var updated = TestFixtures.FlagConfig.Replace("\"defaultVariant\": \"on\"", "\"defaultVariant\": \"off\"");
        var requests = 0;
        _server.Dispatcher = (_, _) => Task.FromResult(Interlocked.Increment(ref requests) == 1
            ? MockResponse.Status(200).Body(TestFixtures.FlagConfig)
            : MockResponse.Status(200).Body(updated));
        var provider = FastProvider(TimeSpan.FromSeconds(10));
        await provider.InitializeAsync(EvaluationContext.Empty);
        Assert.True((await provider.ResolveBooleanValueAsync("my-bool-flag", false)).Value);

        var change = await ReadEventAsync(provider);

        Assert.Equal(ProviderEventTypes.ProviderConfigurationChanged, change.Type);
        Assert.Equal(new[] { "my-bool-flag" }, change.FlagsChanged);
        Assert.False((await provider.ResolveBooleanValueAsync("my-bool-flag", true)).Value);
        await provider.ShutdownAsync();
    }

    [Fact]
    public async Task UnchangedConfiguration_DoesNotAnnounceAChange()
    {
        _server.Dispatcher = (_, _) => Task.FromResult(MockResponse.Status(200).Body(TestFixtures.FlagConfig));
        var provider = FastProvider(TimeSpan.FromSeconds(10));
        await provider.InitializeAsync(EvaluationContext.Empty);

        await Task.Delay(300); // several polls return the identical config

        Assert.False(provider.GetEventChannel().Reader.TryRead(out _));
        await provider.ShutdownAsync();
    }

    // -- events --

    [Fact]
    public async Task StateChanges_AreAnnouncedInOrder()
    {
        var healthy = false;
        var requests = 0;
        _server.Dispatcher = (_, _) =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                return Task.FromResult(MockResponse.Status(200).Body(TestFixtures.FlagConfig));
            }

            return Task.FromResult(Volatile.Read(ref healthy) ? MockResponse.Status(304) : MockResponse.Status(503));
        };
        var provider = FastProvider();
        await provider.InitializeAsync(EvaluationContext.Empty);

        var stale = await ReadEventAsync(provider);
        var error = await ReadEventAsync(provider);
        Volatile.Write(ref healthy, true);
        var ready = await ReadEventAsync(provider);

        Assert.Equal(ProviderEventTypes.ProviderStale, stale.Type);
        Assert.Equal("HTTP 503", stale.Message);
        Assert.Equal(ProviderEventTypes.ProviderError, error.Type);
        Assert.Equal(ErrorType.General, error.ErrorType);
        Assert.Equal(ProviderEventTypes.ProviderReady, ready.Type);
        Assert.Equal("recovered", ready.Message);
        Assert.All(new[] { stale, error, ready }, e => Assert.Equal("dynatrace", e.ProviderName));
        await provider.ShutdownAsync();
    }

    // -- shutdown --

    [Fact]
    public async Task Shutdown_StopsPolling()
    {
        var requests = 0;
        _server.Dispatcher = (_, _) => Task.FromResult(Interlocked.Increment(ref requests) == 1
            ? MockResponse.Status(200).Body(TestFixtures.FlagConfig)
            : MockResponse.Status(304));
        var provider = FastProvider(TimeSpan.FromSeconds(10));
        await provider.InitializeAsync(EvaluationContext.Empty);
        await Task.Delay(200);

        await provider.ShutdownAsync();
        await Task.Delay(100); // let an in-flight poll settle
        var requestsAfterShutdown = _server.RequestCount;
        await Task.Delay(300);

        Assert.Equal(requestsAfterShutdown, _server.RequestCount);
    }

    [Fact]
    public async Task Shutdown_DuringStale_DoesNotEscalateToError()
    {
        var requests = 0;
        _server.Dispatcher = (_, _) => Task.FromResult(Interlocked.Increment(ref requests) == 1
            ? MockResponse.Status(200).Body(TestFixtures.FlagConfig)
            : MockResponse.Status(503));
        var provider = FastProvider(TimeSpan.FromMilliseconds(300));
        await provider.InitializeAsync(EvaluationContext.Empty);
        await AwaitStateAsync(provider, ProviderStatus.Stale, 3_000);

        await provider.ShutdownAsync();
        await Task.Delay(500); // longer than the grace period

        Assert.Equal(ProviderStatus.NotReady, provider.State);
    }

    [Fact]
    public async Task StoreFlags_AfterShutdown_Throws()
    {
        _server.Enqueue(MockResponse.Status(200).Body(TestFixtures.FlagConfig));
        var provider = Provider(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        await provider.InitializeAsync(EvaluationContext.Empty);
        await provider.ShutdownAsync();

        Assert.Throws<InvalidOperationException>(() => provider.StoreFlags(TestFixtures.FlagConfig));
    }

    [Fact]
    public async Task StoreFlags_InvalidConfig_ThrowsParseError()
    {
        _server.Enqueue(MockResponse.Status(200).Body(TestFixtures.FlagConfig));
        var provider = Provider(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        await provider.InitializeAsync(EvaluationContext.Empty);

        Assert.Throws<ParseErrorException>(() => provider.StoreFlags("not-json"));
        await provider.ShutdownAsync();
    }
}

/// <summary>
/// Drives polling through the real OpenFeature API, to prove the events the provider emits are understood by the
/// SDK. The API is a process-wide singleton, so these tests share the serialized collection.
/// </summary>
[Collection("OpenFeature API")]
public class DynatraceProviderPollingSdkTests : IAsyncLifetime
{
    private const string Domain = "dynatrace-polling-tests";

    private readonly MockHttpServer _server = new MockHttpServer();
    private readonly MockHttpServer _metricsServer = new MockHttpServer();

    public Task InitializeAsync()
    {
        _metricsServer.Dispatcher = (_, _) => Task.FromResult(MockResponse.Status(202));
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Api.Instance.ShutdownAsync();

    [Fact]
    public async Task Sdk_ObservesStaleThenReadyAndPicksUpConfigChanges()
    {
        var updated = TestFixtures.FlagConfig.Replace("\"defaultVariant\": \"on\"", "\"defaultVariant\": \"off\"");
        var phase = 0; // 0: healthy, 1: failing, 2: serving an updated config
        var requests = 0;
        _server.Dispatcher = (_, _) =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                return Task.FromResult(MockResponse.Status(200).Body(TestFixtures.FlagConfig));
            }

            return Task.FromResult(Volatile.Read(ref phase) switch
            {
                1 => MockResponse.Status(503),
                2 => MockResponse.Status(200).Body(updated),
                _ => MockResponse.Status(304),
            });
        };
        var stale = new Latch();
        var ready = new Latch();
        var changed = new Latch();
        Api.Instance.AddHandler(ProviderEventTypes.ProviderStale, _ => stale.CountDown());
        Api.Instance.AddHandler(ProviderEventTypes.ProviderConfigurationChanged, _ => changed.CountDown());
        var fetcher = new CdnFetcher(
            DynatraceProvider.BuildCdnUrl(TestFixtures.SdkKey, null),
            _server.CreateClient(),
            new RetryStrategy(new Random(42), (_, _) => Task.CompletedTask));
        var provider = new DynatraceProvider(
            TestFixtures.SdkKey, null, new FlagdCore(), fetcher, _metricsServer.CreateClient(), TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(30));
        await Api.Instance.SetProviderAsync(Domain, provider);
        Api.Instance.AddHandler(ProviderEventTypes.ProviderReady, _ => ready.CountDown());
        var client = Api.Instance.GetClient(Domain);
        Assert.True((await client.GetBooleanValueAsync("my-bool-flag", false)));

        Volatile.Write(ref phase, 1);
        Assert.True(await stale.WaitAsync(TimeSpan.FromSeconds(5)), "the SDK should see the provider go stale");

        Volatile.Write(ref phase, 2);
        Assert.True(await ready.WaitAsync(TimeSpan.FromSeconds(5)), "the SDK should see the provider recover");
        Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(5)), "the SDK should see the configuration change");
        Assert.False(await client.GetBooleanValueAsync("my-bool-flag", true));
    }
}
