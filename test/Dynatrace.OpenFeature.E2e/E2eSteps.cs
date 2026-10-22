using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenFeature;
using OpenFeature.Constant;
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

    // the provider's poll cadence
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

    private string _sdkKey = BaseKey;
    private DynatraceProviderOptions _options = new DynatraceProviderOptions();
    private bool _initFailed;

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
    }

    [BeforeTestRun]
    public static Task StartMockServerAsync() => MockServer.StartAsync();

    [AfterTestRun]
    public static Task StopMockServerAsync() => MockServer.StopAsync();

    [BeforeScenario]
    public Task ResetMockServerAsync() => _mockServer.ResetAsync();

    [AfterScenario]
    public Task ShutDownAsync() => Api.Instance.ShutdownAsync();

    [StepDefinition("a mock server is running")]
    public void AMockServerIsRunning()
    {
        _options = new DynatraceProviderOptions
        {
            ConfigOrigin = _mockServer.Origin,
            MetricsOrigin = _mockServer.Origin,
            MetricsFlushInterval = MetricsFlushInterval,
        };
    }

    [StepDefinition(@"^the SDK key ""([^""]*)""$")]
    public void TheSdkKey(string key) => _sdkKey = key;

    [StepDefinition(@"^the CDN serves the ""([^""]*)"" flag configuration$")]
    public Task TheCdnServesTheFlagConfiguration(string flags) => SetCdnResponsesAsync(new CdnResponse(200, LoadFlags(flags)));

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
        _pollingStartedAtMs = NowMs();
        await ResetCountBaselineAsync();
    }

    // the SDK absorbs a failed initialization, so failure is read from the state it leaves behind
    private async Task RegisterProviderAsync()
    {
        DynatraceProvider provider;
        try
        {
            provider = new DynatraceProvider(_sdkKey, _options);
        }
        catch (ArgumentException)
        {
            _initFailed = true;
            return;
        }

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
