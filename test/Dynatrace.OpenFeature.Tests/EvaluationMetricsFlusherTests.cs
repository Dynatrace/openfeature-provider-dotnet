using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Dynatrace.OpenFeature.Tests;

public class EvaluationMetricsFlusherTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NoRequestWindow = TimeSpan.FromMilliseconds(200);

    private readonly MockHttpServer _metricsServer = new MockHttpServer();

    private EvaluationMetricsFlusher Flusher(
        DynatraceMetricsAggregator aggregator,
        string path = "/v1/metrics",
        TimeSpan? flushInterval = null,
        int capacityThreshold = 1_000,
        HttpClient? httpClient = null)
        => new EvaluationMetricsFlusher(
            aggregator,
            httpClient ?? _metricsServer.CreateClient(),
            TestFixtures.SdkKey,
            new Uri($"https://metrics.example.com{path}"),
            flushInterval ?? TimeSpan.FromMinutes(5),
            capacityThreshold,
            "1.2.3",
            "dynatrace");

    private static DynatraceMetricsAggregator VersionedAggregator(string version = "1741780800")
    {
        var aggregator = new DynatraceMetricsAggregator();
        aggregator.SetVersion(version);
        return aggregator;
    }

    private static DynatraceMetricsAggregator.DrainedBatch SealedBatch(string version, string flagKey)
    {
        var aggregator = VersionedAggregator(version);
        aggregator.Record(flagKey, "on", "static", null, 1741780820L, version);
        return aggregator.Drain();
    }

    [Fact]
    public async Task BuildsTheDocumentedJsonPayload()
    {
        _metricsServer.Enqueue(MockResponse.Status(202));
        var aggregator = VersionedAggregator();
        aggregator.Record("header-redesign", "control", "targeting_match", null, 1741780820L);
        aggregator.Record("deleted-flag", "__default__", "error", "flag_not_found", 1741780820L);

        await Flusher(aggregator).ShutdownAsync();

        var request = await _metricsServer.TakeRequestAsync(Wait);
        Assert.NotNull(request);
        var body = request!.Body;
        Assert.StartsWith("{", body);
        Assert.EndsWith("}", body);
        Assert.DoesNotContain(",,", body);
        Assert.Contains("\"evaluations\":[", body);
        Assert.Contains("\"version\":\"1741780800\"", body);
        Assert.Contains("\"sdkType\":\"server\"", body);
        Assert.Contains("\"sdkVersion\":\"1.2.3\"", body);
        Assert.Contains("\"providerName\":\"dynatrace\"", body);
        Assert.Contains(
            "\"flagKey\":\"header-redesign\",\"variant\":\"control\",\"evaluationReason\":\"targeting_match\","
            + "\"timestampSec\":1741780820,\"count\":1",
            body);
        Assert.Contains(
            "\"flagKey\":\"deleted-flag\",\"variant\":\"__default__\",\"evaluationReason\":\"error\","
            + "\"errorCode\":\"flag_not_found\",\"timestampSec\":1741780820,\"count\":1",
            body);
    }

    [Fact]
    public async Task PayloadIsValidJsonForUnusualFlagKeys()
    {
        _metricsServer.Enqueue(MockResponse.Status(202));
        var aggregator = VersionedAggregator();
        const string flagKey = "weird \"key\" with \\ slashes, <tags>, + and \u00fcn\u00efc\u00f6d\u00e9 \u2603";
        aggregator.Record(flagKey, "v", "static", null, 1741780820L);

        await Flusher(aggregator).ShutdownAsync();

        var request = await _metricsServer.TakeRequestAsync(Wait);
        using var document = JsonDocument.Parse(request!.Body);
        var evaluation = document.RootElement.GetProperty("evaluations")[0];
        Assert.Equal(flagKey, evaluation.GetProperty("flagKey").GetString());
        Assert.Equal(1, evaluation.GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task PostsWithJsonContentTypeAndBearerAuthorization()
    {
        _metricsServer.Enqueue(MockResponse.Status(202));
        var aggregator = VersionedAggregator();
        aggregator.Record("flag", "on", "static", null, 1741780820L);

        await Flusher(aggregator).ShutdownAsync();

        var request = await _metricsServer.TakeRequestAsync(Wait);
        Assert.Equal(HttpMethod.Post, request!.Method);
        Assert.Equal("application/json", request.GetHeader("Content-Type"));
        Assert.Equal($"Bearer {TestFixtures.SdkKey}", request.GetHeader("Authorization"));
    }

    [Fact]
    public async Task TriggerFlush_PostsSealedBatchAsynchronously()
    {
        _metricsServer.Enqueue(MockResponse.Status(202));
        var aggregator = VersionedAggregator();
        aggregator.Record("header-redesign", "control", "targeting_match", null, 1741780820L);
        var flusher = Flusher(aggregator);
        flusher.Start();

        var sealedBatch = aggregator.Drain();
        flusher.EnqueueSealedBatch(sealedBatch);
        flusher.TriggerFlush();

        Assert.Equal(0, aggregator.Size); // live aggregator should be empty after drain
        var request = await _metricsServer.TakeRequestAsync(Wait);
        Assert.NotNull(request);
        Assert.Equal("/v1/metrics", request!.Path);
        Assert.Contains("\"version\":\"1741780800\"", request.Body);

        await flusher.ShutdownAsync();
    }

    [Fact]
    public async Task PostsToTheConfiguredMetricsUrlExactly()
    {
        _metricsServer.Enqueue(MockResponse.Status(202));
        var aggregator = VersionedAggregator();
        aggregator.Record("header-redesign", "control", "targeting_match", null, 1741780820L);

        await Flusher(aggregator, "/custom/metrics").ShutdownAsync();

        var request = await _metricsServer.TakeRequestAsync(Wait);
        Assert.Equal("/custom/metrics", request!.Path);
    }

    [Fact]
    public async Task PeriodicFlush_PostsWithoutAnyOtherTrigger()
    {
        _metricsServer.Enqueue(MockResponse.Status(202));
        var aggregator = VersionedAggregator();
        aggregator.Record("flag", "on", "static", null, 1741780820L);
        var flusher = Flusher(aggregator, flushInterval: TimeSpan.FromMilliseconds(100));

        flusher.Start();

        Assert.NotNull(await _metricsServer.TakeRequestAsync(Wait));
        await flusher.ShutdownAsync();
    }

    [Fact]
    public async Task DropsRecordsWhenNoVersionIsAvailable()
    {
        var aggregator = new DynatraceMetricsAggregator();
        // no version set: dropped
        aggregator.Record("header-redesign", "control", "targeting_match", null, 1741780820L);

        await Flusher(aggregator).ShutdownAsync();

        Assert.Equal(0, aggregator.Size);
        Assert.Null(await _metricsServer.TakeRequestAsync(NoRequestWindow)); // nothing was recorded, nothing is sent
    }

    [Fact]
    public async Task RequeuesTheBatchWhenTheEndpointRejectsIt()
    {
        _metricsServer.Enqueue(MockResponse.Status(500));
        _metricsServer.Enqueue(MockResponse.Status(202));
        var aggregator = VersionedAggregator();
        aggregator.Record("header-redesign", "control", "targeting_match", null, 1741780820L);
        var flusher = Flusher(aggregator, capacityThreshold: 1);
        flusher.Start();

        flusher.OnRecord();

        // the request arrived, so Drain() has run
        var failed = await _metricsServer.TakeRequestAsync(Wait);
        Assert.Equal("/v1/metrics", failed!.Path);
        Assert.Equal(0, aggregator.Size); // a failed batch must not be merged back into the live aggregator

        flusher.TriggerFlush();
        var retry = await _metricsServer.TakeRequestAsync(Wait);
        Assert.NotNull(retry); // the versioned failed batch retries as a sealed batch
        Assert.Contains("\"version\":\"1741780800\"", retry!.Body);

        await flusher.ShutdownAsync();
    }

    [Fact]
    public async Task FailedLiveFlush_RetriesWithTheOriginalVersionAfterAConfigChange()
    {
        _metricsServer.Enqueue(MockResponse.Status(500));
        _metricsServer.Enqueue(MockResponse.Status(202));
        _metricsServer.Enqueue(MockResponse.Status(202));
        var aggregator = VersionedAggregator();
        aggregator.Record("header-redesign", "control", "targeting_match", null, 1741780820L);
        var flusher = Flusher(aggregator, capacityThreshold: 1);
        flusher.Start();

        flusher.OnRecord();
        var failed = await _metricsServer.TakeRequestAsync(Wait);
        Assert.Contains("\"version\":\"1741780800\"", failed!.Body);

        aggregator.SetVersion("1741780900");
        aggregator.Record("new-flag", "on", "static", null, 1741780910L);

        await flusher.ShutdownAsync();

        var retry = await _metricsServer.TakeRequestAsync(Wait);
        Assert.NotNull(retry); // retry of the failed old-version batch
        Assert.Contains("\"version\":\"1741780800\"", retry!.Body);
        Assert.Contains("\"flagKey\":\"header-redesign\"", retry.Body);
        Assert.DoesNotContain("new-flag", retry.Body);

        var live = await _metricsServer.TakeRequestAsync(Wait);
        Assert.NotNull(live); // the new-version live batch
        Assert.Contains("\"version\":\"1741780900\"", live!.Body);
        Assert.Contains("\"flagKey\":\"new-flag\"", live.Body);
        Assert.DoesNotContain("header-redesign", live.Body);
    }

    [Fact]
    public async Task BadRequest_DropsTheBatchAndContinuesWithTheNextSealedBatch()
    {
        _metricsServer.Enqueue(MockResponse.Status(400));
        _metricsServer.Enqueue(MockResponse.Status(202));
        var flusher = Flusher(new DynatraceMetricsAggregator());
        flusher.EnqueueSealedBatch(SealedBatch("1741780800", "bad-payload-flag"));
        flusher.EnqueueSealedBatch(SealedBatch("1741780900", "next-flag"));

        flusher.TriggerFlush();

        var bad = await _metricsServer.TakeRequestAsync(Wait);
        Assert.NotNull(bad); // the non-retryable batch attempt
        Assert.Contains("bad-payload-flag", bad!.Body);
        var next = await _metricsServer.TakeRequestAsync(Wait);
        Assert.NotNull(next); // the flusher continues after a 400
        Assert.Contains("next-flag", next!.Body);

        await flusher.ShutdownAsync();
    }

    [Fact]
    public async Task ServerError_StopsTheCycleAndKeepsOrdering()
    {
        // a failed sealed batch retries before anything newer
        _metricsServer.Enqueue(MockResponse.Status(503));
        _metricsServer.Enqueue(MockResponse.Status(202));
        _metricsServer.Enqueue(MockResponse.Status(202));
        var flusher = Flusher(new DynatraceMetricsAggregator());
        flusher.EnqueueSealedBatch(SealedBatch("1741780800", "first-flag"));
        flusher.EnqueueSealedBatch(SealedBatch("1741780900", "second-flag"));

        flusher.TriggerFlush();
        var failed = await _metricsServer.TakeRequestAsync(Wait);
        Assert.Contains("first-flag", failed!.Body);
        Assert.Null(await _metricsServer.TakeRequestAsync(NoRequestWindow)); // the cycle ended at the failure

        flusher.TriggerFlush();
        var first = await _metricsServer.TakeRequestAsync(Wait);
        var second = await _metricsServer.TakeRequestAsync(Wait);
        Assert.Contains("first-flag", first!.Body);
        Assert.Contains("second-flag", second!.Body);

        await flusher.ShutdownAsync();
    }

    [Fact]
    public async Task Flush_RequeuesTheBatchWhenTheHttpClientThrowsAnUnexpectedException()
    {
        // an undocumented exception must still requeue the drained batch
        var calls = 0;
        _metricsServer.Dispatcher = (_, _) => Interlocked.Increment(ref calls) > 1
            ? Task.FromResult(MockResponse.Status(202))
            : throw new InvalidOperationException("boom");
        var aggregator = VersionedAggregator();
        aggregator.Record("header-redesign", "control", "targeting_match", null, 1741780820L);
        var flusher = Flusher(aggregator);

        flusher.TriggerFlush();
        var attempt = await _metricsServer.TakeRequestAsync(Wait);
        Assert.NotNull(attempt);
        Assert.Equal(0, aggregator.Size); // the versioned failed batch is preserved outside the live aggregator

        flusher.TriggerFlush();
        var retry = await _metricsServer.TakeRequestAsync(Wait);
        Assert.NotNull(retry);
        Assert.Contains("header-redesign", retry!.Body);
        await flusher.ShutdownAsync();
    }

    [Fact]
    public async Task EnqueueSealedBatch_DropsTheBatchWhenTheQueueIsFull()
    {
        // enough 202s for MAX batches + 1 extra delivery after one slot frees up
        for (var i = 0; i < EvaluationMetricsFlusher.MaxSealedBatches + 1; i++)
        {
            _metricsServer.Enqueue(MockResponse.Status(202));
        }

        var flusher = Flusher(new DynatraceMetricsAggregator());
        for (var i = 0; i < EvaluationMetricsFlusher.MaxSealedBatches; i++)
        {
            flusher.EnqueueSealedBatch(SealedBatch($"1741780{i:000}", $"flag-{i}"));
        }

        // one more must be dropped: the queue is full
        flusher.EnqueueSealedBatch(SealedBatch("1741780999", "flag-extra"));

        // flushing MAX batches frees MAX slots
        flusher.Start();
        flusher.TriggerFlush();
        for (var i = 0; i < EvaluationMetricsFlusher.MaxSealedBatches; i++)
        {
            Assert.NotNull(await _metricsServer.TakeRequestAsync(Wait));
        }

        // a new batch must now be accepted: slots were freed
        flusher.EnqueueSealedBatch(SealedBatch("1741781000", "flag-new"));
        await flusher.ShutdownAsync();

        var last = await _metricsServer.TakeRequestAsync(Wait);
        Assert.NotNull(last); // the batch enqueued after the slots freed up must be delivered
        Assert.Contains("flag-new", last!.Body);
        Assert.Null(await _metricsServer.TakeRequestAsync(NoRequestWindow)); // and flag-extra was never queued
    }

    [Fact]
    public async Task EnqueueSealedBatch_IgnoresNullAndEmptyBatches()
    {
        var flusher = Flusher(new DynatraceMetricsAggregator());

        flusher.EnqueueSealedBatch(null);
        flusher.EnqueueSealedBatch(new DynatraceMetricsAggregator().Drain());
        await flusher.ShutdownAsync();

        Assert.Null(await _metricsServer.TakeRequestAsync(NoRequestWindow));
    }

    [Fact]
    public async Task CapacityFlush_DoesNotImmediatelyRetryAfterAFailure()
    {
        _metricsServer.Enqueue(MockResponse.Status(500));
        var aggregator = VersionedAggregator();
        var flusher = Flusher(aggregator, capacityThreshold: 1);
        flusher.Start();

        aggregator.Record("header-redesign", "control", "targeting_match", null, 1741780820L);
        flusher.OnRecord();
        Assert.NotNull(await _metricsServer.TakeRequestAsync(Wait));

        aggregator.Record("header-redesign", "control", "targeting_match", null, 1741780820L);
        flusher.OnRecord();
        Assert.Null(await _metricsServer.TakeRequestAsync(NoRequestWindow)); // the failure opened a cooldown

        _metricsServer.Enqueue(MockResponse.Status(202));
        await flusher.ShutdownAsync();
    }

    [Fact]
    public async Task OnRecord_BelowTheCapacityThreshold_DoesNotFlush()
    {
        var aggregator = VersionedAggregator();
        var flusher = Flusher(aggregator, capacityThreshold: 5);
        flusher.Start();
        aggregator.Record("flag", "on", "static", null, 1741780820L);

        flusher.OnRecord();

        Assert.Null(await _metricsServer.TakeRequestAsync(NoRequestWindow));
        _metricsServer.Enqueue(MockResponse.Status(202));
        await flusher.ShutdownAsync();
    }

    [Fact]
    public async Task OnRecord_BeforeStart_DoesNotFlush()
    {
        var aggregator = VersionedAggregator();
        var flusher = Flusher(aggregator, capacityThreshold: 1);
        aggregator.Record("flag", "on", "static", null, 1741780820L);

        flusher.OnRecord();

        Assert.Null(await _metricsServer.TakeRequestAsync(NoRequestWindow));
    }

    [Fact]
    public async Task Shutdown_IsIdempotentAndStopsFurtherFlushes()
    {
        _metricsServer.Enqueue(MockResponse.Status(202));
        var aggregator = VersionedAggregator();
        aggregator.Record("flag", "on", "static", null, 1741780820L);
        var flusher = Flusher(aggregator);

        await flusher.ShutdownAsync();
        await flusher.ShutdownAsync();
        aggregator.Record("flag", "on", "static", null, 1741780820L);
        flusher.TriggerFlush(); // ignored after shutdown

        Assert.NotNull(await _metricsServer.TakeRequestAsync(Wait));
        Assert.Null(await _metricsServer.TakeRequestAsync(NoRequestWindow));
    }

    [Fact]
    public async Task Start_IsIdempotent()
    {
        _metricsServer.Enqueue(MockResponse.Status(202));
        var aggregator = VersionedAggregator();
        aggregator.Record("flag", "on", "static", null, 1741780820L);
        var flusher = Flusher(aggregator, flushInterval: TimeSpan.FromMilliseconds(100));

        flusher.Start();
        flusher.Start();
        await Task.Delay(400);
        await flusher.ShutdownAsync();

        // a second loop would not change what is sent, only how often; the single batch is delivered exactly once
        Assert.NotNull(await _metricsServer.TakeRequestAsync(Wait));
        Assert.Null(await _metricsServer.TakeRequestAsync(NoRequestWindow));
    }
}
