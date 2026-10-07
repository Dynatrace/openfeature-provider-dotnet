using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Dynatrace.OpenFeature.Tests;

public class DynatraceMetricsAggregatorTests
{
    private static long CountOf(DynatraceMetricsAggregator.DrainedBatch batch)
        => batch.Evaluations.Values.SelectMany(flag => flag.Values).Sum(aggregate => aggregate.Count);

    [Fact]
    public void Drain_ClearsLiveMapAndPreservesPreviousEntries()
    {
        var aggregator = new DynatraceMetricsAggregator();
        aggregator.SetVersion("1741780800");
        aggregator.Record("header-redesign", "control", "targeting_match", null, 1741780820);

        var batch = aggregator.Drain();

        Assert.Single(batch.Evaluations["header-redesign"]);
        Assert.Empty(aggregator.Snapshot());
        Assert.Equal(0, aggregator.Size);
        Assert.Equal("1741780800", batch.Version);
        Assert.Equal("1741780800", aggregator.Version);
    }

    [Fact]
    public void Drain_CapturesTheVersionWithTheBatch()
    {
        var aggregator = new DynatraceMetricsAggregator();
        aggregator.SetVersion("1741780800");
        aggregator.Record("header-redesign", "control", "targeting_match", null, 1741780820);

        var batch = aggregator.Drain();
        aggregator.SetVersion("1741780900");

        Assert.Equal("1741780800", batch.Version);
        Assert.Equal("1741780900", aggregator.Version);
    }

    [Fact]
    public void Record_SameKey_IncrementsTheCount()
    {
        var aggregator = new DynatraceMetricsAggregator();
        aggregator.SetVersion("v1");

        aggregator.Record("flag", "on", "static", null, 1741780820);
        aggregator.Record("flag", "on", "static", null, 1741780820);
        aggregator.Record("flag", "on", "static", null, 1741780830); // a different 10s bucket

        var batch = aggregator.Drain();
        Assert.Equal(2, batch.Evaluations["flag"].Count);
        Assert.Equal(3, CountOf(batch));
    }

    [Fact]
    public void Record_DropsNewFlagsBeyondMaxDistinctFlags()
    {
        var aggregator = new DynatraceMetricsAggregator(2);
        aggregator.SetVersion("1741780800");

        aggregator.Record("flag-a", "v1", "static", null, 1741780820);
        aggregator.Record("flag-b", "v1", "static", null, 1741780820);
        // max distinct flags reached: new flags are dropped
        aggregator.Record("flag-c", "v1", "static", null, 1741780820);
        aggregator.Record("flag-d", "v1", "static", null, 1741780820);
        // existing flags still accept count increments
        aggregator.Record("flag-a", "v1", "static", null, 1741780820);

        Assert.Equal(2, aggregator.Size); // size must not exceed max distinct flags
        var batch = aggregator.Drain();
        Assert.Equal(2, batch.Evaluations["flag-a"].Values.Single().Count); // existing flag keeps accumulating
        Assert.False(batch.Evaluations.ContainsKey("flag-c")); // dropped flag must not appear in the batch
    }

    [Fact]
    public void Record_DropsEvaluationsWhenNoVersionIsSet()
    {
        var aggregator = new DynatraceMetricsAggregator();

        aggregator.Record("header-redesign", "control", "targeting_match", null, 1741780820);

        Assert.Equal(0, aggregator.Size); // evaluations must be dropped when no version is set
        Assert.True(aggregator.Drain().IsEmpty);
    }

    [Fact]
    public void Record_DropsEvaluationsOnVersionMismatch()
    {
        var aggregator = new DynatraceMetricsAggregator();
        aggregator.SetVersion("v1");
        aggregator.Record("flag-a", "on", "static", null, 1741780820, "v1");

        // attempt to record under a different version while the map is non-empty
        aggregator.Record("flag-b", "on", "static", null, 1741780820, "v2");

        Assert.Equal(1, aggregator.Size);
        Assert.Equal("v1", aggregator.Version);
    }

    [Fact]
    public void Record_DropsEvaluationsOnVersionMismatchEvenWhenTheMapIsEmpty()
    {
        var aggregator = new DynatraceMetricsAggregator();
        aggregator.SetVersion("v1");
        aggregator.Drain();

        // map is empty but version is still v1: a mismatch must still be rejected; version transitions are the
        // provider's responsibility via SetVersion()
        aggregator.Record("flag-a", "on", "static", null, 1741780820, "v2");

        Assert.Equal(0, aggregator.Size);
        Assert.Equal("v1", aggregator.Version); // version must not change via Record()
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SetVersion_IgnoresBlankValues(string? blank)
    {
        var aggregator = new DynatraceMetricsAggregator();
        aggregator.SetVersion("v1");

        aggregator.SetVersion(blank);

        Assert.Equal("v1", aggregator.Version);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsNonPositiveMaxDistinctFlags(int max)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DynatraceMetricsAggregator(max));
    }

    [Fact]
    public void Record_UsesATypedKeyWithoutDelimiterCollisions()
    {
        var aggregator = new DynatraceMetricsAggregator();
        aggregator.SetVersion("1741780800");
        aggregator.Record("flag", "a|b", "c", null, 1741780820);
        aggregator.Record("flag", "a", "b|c", null, 1741780820);

        // Both land in distinct slots: a string-concatenated key would collide on the pipe character.
        Assert.Equal(2, aggregator.Snapshot()["flag"].Count);
    }

    [Fact]
    public void Record_DistinguishesMissingFromPresentErrorCode()
    {
        var aggregator = new DynatraceMetricsAggregator();
        aggregator.SetVersion("v1");

        aggregator.Record("flag", "__default__", "error", null, 1741780820);
        aggregator.Record("flag", "__default__", "error", "general", 1741780820);

        Assert.Equal(2, aggregator.Snapshot()["flag"].Count);
    }

    [Fact]
    public async Task ConcurrentRecordAndDrain_DoesNotLoseEvaluations()
    {
        var aggregator = new DynatraceMetricsAggregator();
        aggregator.SetVersion("1741780800");
        const int workerCount = 4;
        const int recordsPerWorker = 1_000;
        var drainedCount = 0L;

        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < recordsPerWorker; i++)
                {
                    aggregator.Record("flag", "variant", "static", null, 1741780820);
                }
            }))
            .ToArray();

        while (!workers.All(worker => worker.IsCompleted))
        {
            drainedCount += CountOf(aggregator.Drain());
            await Task.Delay(1);
        }

        await Task.WhenAll(workers);
        drainedCount += CountOf(aggregator.Drain());

        Assert.Equal(workerCount * recordsPerWorker, drainedCount);
        Assert.Empty(aggregator.Snapshot());
    }
}
