using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Model;
using Reqnroll;
using Xunit;

namespace Dynatrace.OpenFeature.E2e;

/// <summary>
/// Bindings for the harness's gherkin features. Black-box: each scenario binds a fresh provider to its own OpenFeature
/// domain and observes it only through the SDK client and the mock server.
/// </summary>
[Binding]
public sealed class E2eSteps
{
    // well-formed server SDK key every READY arrangement initializes with
    private const string BaseKey = "dt01.server_us_abcdef1234.de848e97a9cc4cc78aae568e65f49a9d_a1b2c3d4e5";

    // a cadence needs at least two ticks to measure a gap
    private const int MinTicksToCompare = 2;

    // stands in for the mock server's authority in a spec'd URL
    private const string MockServerPlaceholder = "<mockserver>";

    // payload a shut-down provider must not react to
    private const string RefetchConfigMessage = @"{""type"":""refetchConfig"",""lastModified"":1704153600}";

    // shortened timings shared by every provider's suite
    private static readonly JsonNode Timings = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "timings.json")))!;

    private static readonly TimeSpan SseConnectedPollInterval = TimeSpan.FromMilliseconds(Timings["SSE_CONNECTED_POLL_INTERVAL_MS"]!.GetValue<int>());

    private static readonly TimeSpan GracePeriod = TimeSpan.FromMilliseconds(Timings["GRACE_PERIOD_MS"]!.GetValue<int>());

    // the provider's poll cadence; the CDN spec runs with SSE off
    private static readonly TimeSpan PollInterval = DynatraceProvider.PollIntervalSseDisconnected;

    // bound on one CDN attempt (KB section 2.1)
    private static readonly TimeSpan RequestTimeout = CdnFetcher.RequestTimeout;

    // longer than the poll cadence, so a refresh is observed
    private static readonly TimeSpan PollTimeout = PollInterval + TimeSpan.FromSeconds(1);

    // covers the time taken by the retries for 5xx codes
    private static readonly TimeSpan RetryTimeout = TimeSpan.FromSeconds(2);

    // window over which a "continues to" assertion must keep holding
    private static readonly TimeSpan StableWindow = TimeSpan.FromSeconds(2);

    // time a state change may take to arrive, since state rides OpenFeature events
    private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(2);

    // slack for something the step just caused, and the window for asserting its absence
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

    // how long to wait for a provider event the step just caused
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(5);

    // how long to wait for the provider to open its SSE stream
    private static readonly TimeSpan SseConnectTimeout = TimeSpan.FromSeconds(5);

    // generous, since the event source's reconnect backoff grows with each failed attempt
    private static readonly TimeSpan SseReconnectTimeout = TimeSpan.FromSeconds(45);

    // how far before a tick an SSE re-fetch is triggered, so it is still in flight then
    private static readonly TimeSpan ShortlyBefore = TimeSpan.FromMilliseconds(500);

    // slack added when sleeping to a tick boundary, so the tick has fired by the time we look
    private static readonly TimeSpan TickMargin = TimeSpan.FromMilliseconds(500);

    // cap on waiting for expected requests; a fetch is at most two bounded attempts
    private static readonly TimeSpan CountTimeout = TimeSpan.FromTicks(RequestTimeout.Ticks * 3);

    // how long an exact request count must keep holding, so a stray extra request still fails
    private static readonly TimeSpan CountSettle = TimeSpan.FromSeconds(1);

    // tolerance on a measured interval; wide enough for CI jitter, narrow enough to catch a wrongly anchored cadence
    private static readonly TimeSpan CadenceTolerance = TimeSpan.FromMilliseconds(1500);

    // requests closer than this belong to the same poll tick (a retry follows one request timeout later)
    private static readonly TimeSpan SameTickGap = TimeSpan.FromTicks(PollInterval.Ticks / 2);

    // cap on waiting for the first poll tick: one interval plus a whole fetch
    private static readonly TimeSpan FirstTickTimeout = PollInterval + CountTimeout;

    // outlives a whole fetch (two attempts, each abandoned at the timeout), so it spans a tick
    private static readonly TimeSpan SlowResponseDelay = TimeSpan.FromTicks(RequestTimeout.Ticks * 4);

    // takes real time without timing out, so fetch start and fetch end are far enough apart to tell apart
    private static readonly TimeSpan SlowButWithinTimeoutDelay = RequestTimeout - TimeSpan.FromSeconds(1);

    // no periodic metrics flush during a scenario
    private static readonly TimeSpan MetricsFlushInterval = TimeSpan.FromHours(1);

    // how often WaitUntilAsync re-checks its condition
    private static readonly TimeSpan WaitCheckInterval = TimeSpan.FromMilliseconds(100);

    // how often AssertHoldsForAsync re-checks its condition
    private static readonly TimeSpan HoldCheckInterval = TimeSpan.FromMilliseconds(50);

    private static int _scenarioSeq;

    private readonly MockServer _mockServer = MockServer.Current!;
    private readonly string _domain = $"e2e-test-{Interlocked.Increment(ref _scenarioSeq)}";
    private readonly FeatureClient _client;

    // provider events seen on this scenario's client, in arrival order; guarded by itself
    private readonly List<(ProviderEventTypes Type, ProviderEventPayload? Payload)> _events = new List<(ProviderEventTypes, ProviderEventPayload?)>();

    private string _sdkKey = BaseKey;
    private DynatraceProviderOptions _options = new DynatraceProviderOptions();
    private DynatraceProvider? _provider;
    private bool _initFailed;

    // index into _events; each "a PROVIDER_* event is emitted" step consumes one
    private int _eventCursor;

    // baseline for "further request" counts; reset by arrangement and every clock-advancing step
    private int _countBaseline;

    // when initialization returned; every poll tick boundary is derived from it
    private long _pollingStartedAtMs;

    // kept so a later step can layer a delay onto them
    private List<CdnResponse> _cdnResponses = new List<CdnResponse>();

    private TimeSpan _retryAfter = TimeSpan.Zero;

    public E2eSteps()
    {
        _client = Api.Instance.GetClient(_domain);
        foreach (var type in new[]
                 {
                     ProviderEventTypes.ProviderReady, ProviderEventTypes.ProviderError,
                     ProviderEventTypes.ProviderStale, ProviderEventTypes.ProviderConfigurationChanged,
                 })
        {
            _client.AddHandler(type, payload =>
            {
                lock (_events)
                {
                    _events.Add((type, payload));
                }
            });
        }
    }

    [BeforeTestRun]
    public static Task StartMockServerAsync() => MockServer.StartAsync();

    [AfterTestRun]
    public static Task StopMockServerAsync() => MockServer.StopAsync();

    // reset does not wait out a stream the previous scenario's provider is still tearing down
    [BeforeScenario]
    public async Task ResetMockServerAsync()
    {
        await _mockServer.ResetAsync();
        await WaitUntilAsync(Settle, async () => await _mockServer.CountSseClientsAsync() == 0);
    }

    [AfterScenario]
    public Task ShutDownAsync() => Api.Instance.ShutdownAsync();

    [StepDefinition("a mock server is running")]
    public void AMockServerIsRunning() => UseMockServer(sseEnabled: false);

    [StepDefinition("a mock server with SSE enabled is running")]
    public void AMockServerWithSseEnabledIsRunning() => UseMockServer(sseEnabled: true);

    [StepDefinition("SSE is disabled by configuration")]
    public void SseIsDisabledByConfiguration() => _options.SseEnabled = false;

    [StepDefinition(@"^the SDK key ""([^""]*)""$")]
    public void TheSdkKey(string key) => _sdkKey = key;

    [StepDefinition(@"^the CDN serves the ""([^""]*)"" flag configuration$")]
    public Task TheCdnServesTheFlagConfiguration(string flags) => SetCdnResponsesAsync(new CdnResponse(200, LoadFlags(flags)));

    [StepDefinition(@"^the CDN serves the ""([^""]*)"" flag configuration advertising an SSE stream at url ""([^""]*)""$")]
    public Task TheCdnServesTheFlagConfigurationAdvertisingAnSseStream(string flags, string url)
        => SetCdnResponsesAsync(new CdnResponse(200, AdvertiseSseUrl(LoadFlags(flags), url)));

    [StepDefinition(@"^an initialized, READY provider serving the ""([^""]*)"" flag configuration$")]
    public Task AnInitializedReadyProvider(string flags) => ArrangeReadyProviderAsync(new CdnResponse(200, LoadFlags(flags)));

    [StepDefinition(@"^an initialized, READY provider serving the ""([^""]*)"" flag configuration with ""([^""]*)"" header (""[^""]*""|'[^']*')$")]
    public Task AnInitializedReadyProviderWithHeader(string flags, string header, string value)
        => ArrangeReadyProviderAsync(new CdnResponse(200, LoadFlags(flags)).WithHeader(header, Unquote(value)));

    [StepDefinition("the CDN responds slower than the request timeout")]
    public Task TheCdnRespondsSlowerThanTheRequestTimeout() => DelayCdnResponsesAsync(SlowResponseDelay);

    [StepDefinition("the CDN responds slowly but within the request timeout")]
    public Task TheCdnRespondsSlowlyButWithinTheRequestTimeout() => DelayCdnResponsesAsync(SlowButWithinTimeoutDelay);

    [StepDefinition("the provider is initialized")]
    public async Task TheProviderIsInitialized()
    {
        await RegisterProviderAsync();

        // anchor, not baseline: a scenario with no arrangement counts the initialization's requests too
        _pollingStartedAtMs = NowMs();
    }

    [StepDefinition(@"^the CDN responds with status (\d+)$")]
    public Task TheCdnRespondsWithStatus(int status) => SetCdnResponsesAsync(new CdnResponse(status));

    // unquoted null is an absent body, as opposed to the empty-string body ""
    [StepDefinition(@"^the CDN responds with status (\d+) and body (""[^""]*""|null)$")]
    public Task TheCdnRespondsWithStatusAndBody(int status, string body) => SetCdnResponsesAsync(
        body == "null" ? new CdnResponse(status) : new CdnResponse(status, Unquote(body)));

    [StepDefinition(@"^the CDN responds with status (\d+) and the ""([^""]*)"" flag configuration with ""([^""]*)"" header (""[^""]*""|'[^']*')$")]
    public Task TheCdnRespondsWithStatusAndFlagsWithHeader(int status, string flags, string header, string value)
        => SetCdnResponsesAsync(new CdnResponse(status, LoadFlags(flags)).WithHeader(header, Unquote(value)));

    [StepDefinition(@"^the CDN responds (\d+) then (\d+) with the ""([^""]*)"" configuration$")]
    public Task TheCdnRespondsThenWith(int first, int second, string flags)
        => SetCdnResponsesAsync(new CdnResponse(first), new CdnResponse(second, LoadFlags(flags)));

    [StepDefinition(@"^the CDN responds with status (\d+) and Retry-After (\d+) seconds$")]
    public Task TheCdnRespondsWithRetryAfter(int status, int seconds)
    {
        _retryAfter = TimeSpan.FromSeconds(seconds);
        return SetCdnResponsesAsync(new CdnResponse(status).WithHeader("Retry-After", seconds.ToString()));
    }

    [StepDefinition("polling triggers a configuration refetch")]
    public async Task PollingTriggersAConfigurationRefetch()
    {
        var baseline = await _mockServer.CountCdnRequestsAsync();
        await AssertEventuallyAsync(PollTimeout, async () => await _mockServer.CountCdnRequestsAsync() > baseline);
    }

    // baseline is taken before sleeping, so the next assertion counts what these intervals produced
    [StepDefinition(@"^(\d+) poll intervals? elapses?$")]
    public async Task PollIntervalsElapse(int intervals)
    {
        await ResetCountBaselineAsync();
        await SleepUntilAsync(NextTickAtMs() + ((intervals - 1) * (long)PollInterval.TotalMilliseconds) + (long)TickMargin.TotalMilliseconds);
    }

    // the next cadence-anchored tick specifically: a Retry-After window expiring does not itself fetch
    [StepDefinition("the next poll tick arrives")]
    public async Task TheNextPollTickArrives()
    {
        await ResetCountBaselineAsync();
        await SleepUntilAsync(NextTickAtMs() + (long)TickMargin.TotalMilliseconds);
    }

    [StepDefinition("the Retry-After window expires")]
    public async Task TheRetryAfterWindowExpires()
    {
        await ResetCountBaselineAsync();
        await Task.Delay(_retryAfter + TickMargin);
    }

    // a blip: the backend drops the stream and refuses connections for the outage, then accepts them again
    [StepDefinition(@"^the SSE stream drops for (\d+) seconds and reconnects$")]
    public async Task TheSseStreamDropsAndReconnects(int outageSeconds)
    {
        await _mockServer.DropSseAsync(TimeSpan.FromSeconds(outageSeconds));
        Assert.True(
            await WaitUntilAsync(SseReconnectTimeout, async () => await _mockServer.CountSseClientsAsync() >= 1),
            $"SSE did not reconnect after the {outageSeconds}s outage");
    }

    // a sustained outage: connections stay refused until reset
    [StepDefinition("the SSE stream drops and stays down")]
    public Task TheSseStreamDropsAndStaysDown() => _mockServer.DropSseAsync();

    // stops early once the message has triggered a CDN request
    [StepDefinition(@"^the server emits an SSE message (.*)$")]
    public async Task TheServerEmitsAnSseMessage(string payload)
    {
        var baseline = await _mockServer.CountCdnRequestsAsync();
        await _mockServer.EmitSseAsync(Unquote(payload));
        await WaitUntilAsync(Settle, async () => await _mockServer.CountCdnRequestsAsync() > baseline);
    }

    [StepDefinition("the GRACE_PERIOD expires")]
    public Task TheGracePeriodExpires()
        => WaitUntilAsync(GracePeriod + Settle, () => Task.FromResult(_client.ProviderStatus == ProviderStatus.Error));

    [StepDefinition("the provider is shut down")]
    public Task TheProviderIsShutDown() => Api.Instance.ShutdownAsync();

    // occupies the fetch slot just before a tick, so the tick has something to contend with
    [StepDefinition("an SSE re-fetch is triggered shortly before the next poll tick")]
    public async Task AnSseRefetchIsTriggeredShortlyBeforeTheNextPollTick()
    {
        await SleepUntilAsync(NextTickAtMs() - (long)ShortlyBefore.TotalMilliseconds);
        var before = await _mockServer.CountCdnRequestsAsync();

        // driven directly: a live stream would switch the provider onto the SSE-connected cadence
        var pollingManager = _provider?.PollingManager;
        Assert.NotNull(pollingManager);
        pollingManager!.RequestOutOfBandFetch();

        // wait for it to reach the CDN, or it is miscounted as a request the contended tick made
        await AssertEventuallyAsync(ShortlyBefore, async () => await _mockServer.CountCdnRequestsAsync() > before);
    }

    [StepDefinition("initialization succeeds")]
    public void InitializationSucceeds() => Assert.False(_initFailed, "expected initialization to succeed");

    [StepDefinition("initialization fails")]
    public void InitializationFails() => Assert.True(_initFailed, "expected initialization to fail");

    // state reaches the client over an event, so allow it a moment
    [StepDefinition(@"^the provider state is ""([^""]*)""$")]
    public async Task TheProviderStateIs(string state)
    {
        var want = ParseStatus(state);
        await WaitUntilAsync(StateTimeout, () => Task.FromResult(_client.ProviderStatus == want));
        Assert.Equal(want, _client.ProviderStatus);
    }

    [StepDefinition(@"^a (\w+) event is emitted$")]
    public Task AProviderEventIsEmitted(string eventName) => ConsumeEventAsync(ParseEventType(eventName));

    // whole scenario, not from the cursor: a STALE that already recovered must still fail
    [StepDefinition(@"^no (\w+) event is emitted$")]
    public void NoProviderEventIsEmitted(string eventName)
    {
        var type = ParseEventType(eventName);
        lock (_events)
        {
            Assert.False(_events.Any(e => e.Type == type), $"unexpected {eventName} event was emitted");
        }
    }

    [StepDefinition(@"^a PROVIDER_CONFIGURATION_CHANGED event is emitted with changed flag ""([^""]*)""$")]
    public async Task AConfigurationChangedEventIsEmittedWithChangedFlag(string flag)
    {
        var payload = await ConsumeEventAsync(ProviderEventTypes.ProviderConfigurationChanged);
        Assert.Contains(flag, payload?.FlagsChanged ?? new List<string>());
    }

    // the backend serves a single stream path, so a live subscriber is a stream to that path
    [StepDefinition(@"^the provider has an SSE connection to ""([^""]*)""$")]
    public async Task TheProviderHasAnSseConnectionTo(string path)
    {
        Assert.Equal(MockServer.SsePath, path);
        Assert.True(
            await WaitUntilAsync(SseConnectTimeout, async () => await _mockServer.CountSseClientsAsync() == 1),
            $"provider did not open an SSE connection to {path}");
    }

    // held, so a connection attempt in flight is not mistaken for none at all
    [StepDefinition("the provider opens no SSE connection")]
    public Task TheProviderOpensNoSseConnection() => AssertHoldsForAsync(
        Settle, async () => await _mockServer.CountSseClientsAsync() == 0, "provider unexpectedly opened an SSE connection");

    [StepDefinition("the provider closes the SSE connection")]
    public async Task TheProviderClosesTheSseConnection() => Assert.True(
        await WaitUntilAsync(Settle, async () => await _mockServer.CountSseClientsAsync() == 0),
        "provider did not close the SSE connection");

    [StepDefinition("a message after shutdown triggers no CDN request")]
    public async Task AMessageAfterShutdownTriggersNoCdnRequest()
    {
        var baseline = await _mockServer.CountCdnRequestsAsync();
        await _mockServer.EmitSseAsync(RefetchConfigMessage);
        await Task.Delay(StableWindow);
        Assert.Equal(baseline, await _mockServer.CountCdnRequestsAsync());
    }

    // the cadence is not observable black-box, so assert its consequence: no fetch within the aggressive cadence
    [StepDefinition("the active poll interval is the SSE_CONNECTED_POLL_INTERVAL")]
    public async Task TheActivePollIntervalIsTheSseConnectedPollInterval()
    {
        Assert.True(PollTimeout < SseConnectedPollInterval, "observation window must be shorter than the interval under test");

        // the connect catch-up fetch must be in before the baseline, or it arrives mid-observation
        await AwaitSseSettledAsync();
        var baseline = await _mockServer.CountCdnRequestsAsync();
        await Task.Delay(PollTimeout);
        Assert.True(baseline == await _mockServer.CountCdnRequestsAsync(),
            $"provider kept fetching within {PollTimeout.TotalSeconds}s, so polling did not relax");
    }

    // asserted by its consequence too: a fetch must arrive within the aggressive cadence
    [StepDefinition("the active poll interval is the SSE_DISCONNECTED_POLL_INTERVAL")]
    public async Task TheActivePollIntervalIsTheSseDisconnectedPollInterval()
    {
        var baseline = await _mockServer.CountCdnRequestsAsync();
        await AssertEventuallyAsync(PollInterval, async () => await _mockServer.CountCdnRequestsAsync() > baseline);
    }

    // a fetch the provider was told to make is in flight by now, so wait for the count before insisting on it
    [StepDefinition(@"^the CDN received (\d+) requests$")]
    public async Task TheCdnReceivedRequests(int count)
    {
        await WaitUntilAsync(Settle, async () => await _mockServer.CountCdnRequestsAsync() >= count);
        Assert.Equal(count, await _mockServer.CountCdnRequestsAsync());
    }

    [StepDefinition(@"^the initial CDN request has no ""([^""]*)"" header$")]
    public async Task TheInitialCdnRequestHasNoHeader(string header)
        => Assert.False((await NthCdnRequestAsync(1)).HasHeader(header), $"initial request unexpectedly carried {header}");

    [StepDefinition(@"^the (\d+)\. CDN request has the ""([^""]*)"" header (""[^""]*""|'[^']*')$")]
    public async Task TheCdnRequestHasTheHeader(int index, string header, string value)
        => Assert.Equal(Unquote(value), (await NthCdnRequestAsync(index)).GetHeader(header));

    // must settle rather than merely be reached: the interesting assertions here are upper bounds
    [StepDefinition(@"^the CDN has received (\d+) further requests?$")]
    public async Task TheCdnHasReceivedFurtherRequests(int count)
    {
        if (count > 0)
        {
            await AssertEventuallyAsync(CountTimeout, async () => await CountFurtherRequestsAsync() >= count);
        }

        await AssertHoldsForAsync(CountSettle, async () => await CountFurtherRequestsAsync() == count,
            $"expected {count} further CDN request(s)");
        await ResetCountBaselineAsync();
    }

    // anchored to when each fetch was issued, not when it finished
    [StepDefinition("consecutive poll ticks are one poll interval apart")]
    public async Task ConsecutivePollTicksAreOnePollIntervalApart()
    {
        var ticks = await CollectTickTimesAsync();
        Assert.True(ticks.Count >= MinTicksToCompare, $"need at least {MinTicksToCompare} poll ticks to compare, saw {ticks.Count}");
        for (var i = 1; i < ticks.Count; i++)
        {
            AssertCadence(ticks[i] - ticks[i - 1], $"gap between poll tick {i} and {i + 1}");
        }
    }

    // a slow initial fetch is what separates "started" from "completed" enough to tell them apart
    [StepDefinition("the first poll tick is one poll interval after initialization completed")]
    public async Task TheFirstPollTickIsOnePollIntervalAfterInitializationCompleted()
    {
        var anchor = _pollingStartedAtMs;
        long? firstTick = null;
        await AssertEventuallyAsync(FirstTickTimeout, async () => (firstTick = await FirstRequestAfterAsync(anchor)) is not null);
        AssertCadence(firstTick!.Value - anchor, "gap between initialization completing and the first poll tick");
    }

    [StepDefinition(@"^flag ""([^""]*)"" evaluates to (\w+)$")]
    public async Task FlagEvaluatesTo(string flag, string expected)
        => Assert.True(await IsFlagValueAsync(flag, bool.Parse(expected)), $"flag {flag} did not evaluate to {expected}");

    [StepDefinition(@"^flag ""([^""]*)"" eventually evaluates to (\w+)$")]
    public Task FlagEventuallyEvaluatesTo(string flag, string expected)
        => AssertEventuallyAsync(RetryTimeout, () => IsFlagValueAsync(flag, bool.Parse(expected)));

    [StepDefinition(@"^flag ""([^""]*)"" continues to evaluate to (\w+)$")]
    public async Task FlagContinuesToEvaluateTo(string flag, string expected)
    {
        var want = bool.Parse(expected);
        await AssertHoldsForAsync(StableWindow, () => IsFlagValueAsync(flag, want), $"flag {flag} changed away from {expected}");
    }

    [StepDefinition(@"^evaluating flag ""([^""]*)"" returns a Flag-Not-Found error$")]
    public async Task EvaluatingFlagReturnsAFlagNotFoundError(string flag)
        => Assert.Equal(ErrorType.FlagNotFound, (await _client.GetBooleanDetailsAsync(flag, false)).ErrorType);

    // counts and tick boundaries run from the end of the arrangement
    private async Task ArrangeReadyProviderAsync(CdnResponse response)
    {
        await SetCdnResponsesAsync(response);
        await RegisterProviderAsync();
        Assert.Equal(ProviderStatus.Ready, _client.ProviderStatus);
        await AwaitSseSettledAsync();
        _pollingStartedAtMs = NowMs();
        await ResetCountBaselineAsync();
    }

    private void UseMockServer(bool sseEnabled) => _options = new DynatraceProviderOptions
    {
        ConfigOrigin = _mockServer.Origin,
        MetricsOrigin = _mockServer.Origin,
        MetricsFlushInterval = MetricsFlushInterval,
        SseEnabled = sseEnabled,
    };

    // waits for the stream and its catch-up fetch, so READY scenarios never race it; a no-op with SSE off
    private async Task AwaitSseSettledAsync()
    {
        if (!_options.SseEnabled)
        {
            return;
        }

        Assert.True(
            await WaitUntilAsync(SseConnectTimeout, async () => await _mockServer.CountSseClientsAsync() >= 1),
            "provider did not open its SSE stream");

        // fetch 1 comes from initialization, fetch 2 from the successful connect
        Assert.True(
            await WaitUntilAsync(Settle, async () => await _mockServer.CountCdnRequestsAsync() >= 2),
            "SSE connected but pulled no catch-up fetch");
    }

    // the next recorded event of the type; advances the cursor so repeated steps consume distinct events
    private async Task<ProviderEventPayload?> ConsumeEventAsync(ProviderEventTypes type)
    {
        var index = -1;
        await WaitUntilAsync(EventTimeout, () => Task.FromResult((index = FindNextEvent(type)) >= 0));
        Assert.True(index >= 0, $"no {type} event emitted within {EventTimeout.TotalSeconds}s");
        lock (_events)
        {
            _eventCursor = index + 1;
            return _events[index].Payload;
        }
    }

    private int FindNextEvent(ProviderEventTypes type)
    {
        lock (_events)
        {
            for (var i = _eventCursor; i < _events.Count; i++)
            {
                if (_events[i].Type == type)
                {
                    return i;
                }
            }

            return -1;
        }
    }

    // rewrites a flag configuration fixture to advertise a direct SSE url
    private string AdvertiseSseUrl(string configJson, string url)
    {
        var config = JsonNode.Parse(configJson)!.AsObject();
        config["eventStreams"] = new JsonArray(new JsonObject
        {
            ["type"] = "sse",
            ["url"] = url.Replace(MockServerPlaceholder, new Uri(_mockServer.Origin).Authority),
        });
        return config.ToJsonString();
    }

    // the SDK absorbs a failed initialization, so failure is read from the state it leaves behind
    private async Task RegisterProviderAsync()
    {
        DynatraceProvider provider;
        try
        {
            provider = new DynatraceProvider(_sdkKey, _options, SseConnectedPollInterval, GracePeriod);
        }
        catch (ArgumentException)
        {
            _initFailed = true;
            return;
        }

        _provider = provider;
        await Api.Instance.SetProviderAsync(_domain, provider);
        _initFailed = _client.ProviderStatus != ProviderStatus.Ready;
    }

    private Task SetCdnResponsesAsync(params CdnResponse[] responses)
    {
        _cdnResponses = responses.ToList();
        return _mockServer.SetCdnResponsesAsync(_cdnResponses);
    }

    private Task DelayCdnResponsesAsync(TimeSpan delay)
    {
        Assert.NotEmpty(_cdnResponses);
        foreach (var response in _cdnResponses)
        {
            response.DelayMs = (int)delay.TotalMilliseconds;
        }

        return _mockServer.SetCdnResponsesAsync(_cdnResponses);
    }

    private async Task ResetCountBaselineAsync() => _countBaseline = await _mockServer.CountCdnRequestsAsync();

    private async Task<int> CountFurtherRequestsAsync() => await _mockServer.CountCdnRequestsAsync() - _countBaseline;

    private async Task<RecordedRequest> NthCdnRequestAsync(int position)
    {
        var requests = await _mockServer.FetchCdnRequestsAsync();
        Assert.True(requests.Count >= position, $"expected at least {position} CDN request(s), saw {requests.Count}");
        return requests[position - 1];
    }

    private long NextTickAtMs()
    {
        var period = (long)PollInterval.TotalMilliseconds;
        var elapsed = NowMs() - _pollingStartedAtMs;
        return _pollingStartedAtMs + (((elapsed / period) + 1) * period);
    }

    private async Task<long?> FirstRequestAfterAsync(long epochMs)
        => (await _mockServer.FetchCdnRequestsAsync()).Select(r => (long?)r.ReceivedAtMs).FirstOrDefault(at => at > epochMs);

    // arrival times of the first request of each tick; the rest of a run are its retries
    private async Task<List<long>> CollectTickTimesAsync()
    {
        var ticks = new List<long>();
        long? previous = null;
        foreach (var request in await _mockServer.FetchCdnRequestsAsync())
        {
            if (previous is null || request.ReceivedAtMs - previous >= SameTickGap.TotalMilliseconds)
            {
                ticks.Add(request.ReceivedAtMs);
            }

            previous = request.ReceivedAtMs;
        }

        return ticks;
    }

    private static void AssertCadence(long measuredMs, string what)
    {
        var expected = (long)PollInterval.TotalMilliseconds;
        Assert.True(Math.Abs(measuredMs - expected) <= CadenceTolerance.TotalMilliseconds,
            $"{what} was {measuredMs}ms, expected {expected}ms +/- {CadenceTolerance.TotalMilliseconds}ms");
    }

    // defaults to the opposite, so a missing flag can never pass as the expected value
    private async Task<bool> IsFlagValueAsync(string flag, bool expected)
        => await _client.GetBooleanValueAsync(flag, !expected) == expected;

    private static string LoadFlags(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "flags", name + ".json"));

    private static ProviderEventTypes ParseEventType(string name) => name switch
    {
        "PROVIDER_READY" => ProviderEventTypes.ProviderReady,
        "PROVIDER_ERROR" => ProviderEventTypes.ProviderError,
        "PROVIDER_STALE" => ProviderEventTypes.ProviderStale,
        "PROVIDER_CONFIGURATION_CHANGED" => ProviderEventTypes.ProviderConfigurationChanged,
        _ => throw new ArgumentException($"unknown provider event {name}"),
    };

    private static ProviderStatus ParseStatus(string state) => state switch
    {
        "READY" => ProviderStatus.Ready,
        "NOT_READY" => ProviderStatus.NotReady,
        "ERROR" => ProviderStatus.Error,
        "STALE" => ProviderStatus.Stale,
        "FATAL" => ProviderStatus.Fatal,
        _ => throw new ArgumentException($"unknown provider state {state}"),
    };

    // single quotes delimit values that contain double quotes, e.g. ETags
    private static string Unquote(string quoted) => quoted.Substring(1, quoted.Length - 2);

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static async Task<bool> WaitUntilAsync(TimeSpan timeout, Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(WaitCheckInterval);
        }

        return await condition();
    }

    private static async Task AssertEventuallyAsync(TimeSpan timeout, Func<Task<bool>> condition)
        => Assert.True(await WaitUntilAsync(timeout, condition), $"condition not met within {timeout.TotalSeconds}s");

    private static async Task AssertHoldsForAsync(TimeSpan duration, Func<Task<bool>> condition, string message)
    {
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            Assert.True(await condition(), message);
            await Task.Delay(HoldCheckInterval);
        }

        Assert.True(await condition(), message);
    }

    private static Task SleepUntilAsync(long epochMs)
    {
        var remaining = epochMs - NowMs();
        return remaining > 0 ? Task.Delay(TimeSpan.FromMilliseconds(remaining)) : Task.CompletedTask;
    }
}
