using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenFeature.Constant;
using OpenFeature.Model;

namespace Dynatrace.OpenFeature.Tests;

public class DynatraceMetricsHookTests
{
    private static readonly Func<DateTimeOffset> Clock823 = () => DateTimeOffset.FromUnixTimeSeconds(1741780823);
    private static readonly Func<DateTimeOffset> Clock829 = () => DateTimeOffset.FromUnixTimeSeconds(1741780829);

    private static DynatraceMetricsAggregator VersionedAggregator()
    {
        var aggregator = new DynatraceMetricsAggregator();
        aggregator.SetVersion("1741780800");
        return aggregator;
    }

    private static HookContext<object> BooleanHookContext(string flagKey)
        => new HookContext<object>(
            flagKey,
            false,
            FlagValueType.Boolean,
            new ClientMetadata("test-client", "1.0"),
            new Metadata(DynatraceProvider.ProviderName),
            EvaluationContext.Empty);

    private static FlagEvaluationDetails<object> Details(
        string flagKey,
        string? variant = null,
        string? reason = null,
        ErrorType errorType = ErrorType.None,
        ImmutableMetadata? metadata = null)
        => new FlagEvaluationDetails<object>(flagKey, false, errorType, reason, variant, null, metadata);

    private static ValueTask Run(DynatraceMetricsHook hook, FlagEvaluationDetails<object> details)
        => hook.FinallyAsync(BooleanHookContext(details.FlagKey), details, new Dictionary<string, object>());

    [Fact]
    public async Task AggregatesEvaluationsByDocumentedDimensionKey()
    {
        var aggregator = VersionedAggregator();
        var hook = new DynatraceMetricsHook(aggregator, Clock823);
        var details = Details("header-redesign", "control", Reason.TargetingMatch);

        await Run(hook, details);
        await Run(hook, details);

        var aggregate = aggregator.Snapshot()["header-redesign"][new EvaluationMetricKey("control", "targeting_match", null, 1741780820)];
        Assert.Equal("control", aggregate.Variant);
        Assert.Equal("targeting_match", aggregate.Reason);
        Assert.Null(aggregate.ErrorCode);
        Assert.Equal(1741780820, aggregate.TimestampSec);
        Assert.Equal(2, aggregate.Count);
    }

    [Fact]
    public async Task UsesDefaultSentinelAndLowercaseErrorCodeForErrors()
    {
        var aggregator = VersionedAggregator();
        var hook = new DynatraceMetricsHook(aggregator, Clock829);

        await Run(hook, Details("deleted-flag", errorType: ErrorType.FlagNotFound));

        var aggregate = aggregator.Snapshot()["deleted-flag"][new EvaluationMetricKey("__default__", "error", "flag_not_found", 1741780820)];
        Assert.Equal("__default__", aggregate.Variant);
        Assert.Equal("error", aggregate.Reason);
        Assert.Equal("flag_not_found", aggregate.ErrorCode);
        Assert.Equal(1741780820, aggregate.TimestampSec);
        Assert.Equal(1, aggregate.Count);
    }

    [Fact]
    public async Task UsesDefaultSentinelForDefaultReason()
    {
        var aggregator = VersionedAggregator();
        var hook = new DynatraceMetricsHook(aggregator, Clock829);

        await Run(hook, Details("fallback-flag", "off", Reason.Default));

        var aggregate = aggregator.Snapshot()["fallback-flag"][new EvaluationMetricKey("__default__", "default", null, 1741780820)];
        Assert.Equal("__default__", aggregate.Variant);
        Assert.Equal("default", aggregate.Reason);
    }

    [Fact]
    public async Task ForcesErrorReasonAndDefaultSentinelForErrors()
    {
        var aggregator = VersionedAggregator();
        var hook = new DynatraceMetricsHook(aggregator, Clock829);

        await Run(hook, Details("mismatched-type", "on", Reason.TargetingMatch, ErrorType.TypeMismatch));

        var aggregate = aggregator.Snapshot()["mismatched-type"][new EvaluationMetricKey("__default__", "error", "type_mismatch", 1741780820)];
        Assert.Equal("__default__", aggregate.Variant);
        Assert.Equal("error", aggregate.Reason);
        Assert.Equal("type_mismatch", aggregate.ErrorCode);
    }

    [Theory]
    [InlineData(ErrorType.ProviderNotReady, "provider_not_ready")]
    [InlineData(ErrorType.FlagNotFound, "flag_not_found")]
    [InlineData(ErrorType.ParseError, "parse_error")]
    [InlineData(ErrorType.TypeMismatch, "type_mismatch")]
    [InlineData(ErrorType.TargetingKeyMissing, "targeting_key_missing")]
    [InlineData(ErrorType.InvalidContext, "invalid_context")]
    [InlineData(ErrorType.General, "general")]
    [InlineData(ErrorType.ProviderFatal, "provider_fatal")]
    public async Task MapsEveryErrorTypeToItsLineProtocolName(ErrorType errorType, string expected)
    {
        var aggregator = VersionedAggregator();
        var hook = new DynatraceMetricsHook(aggregator, Clock829);

        await Run(hook, Details("flag", errorType: errorType));

        var aggregate = aggregator.Snapshot()["flag"].Values.Single();
        Assert.Equal(expected, aggregate.ErrorCode);
    }

    [Theory]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    [InlineData("   ", "unknown")]
    [InlineData("CACHED", "cached")]
    [InlineData("STATIC", "static")]
    [InlineData("SomethingNew", "somethingnew")] // an unknown reason is still lowercased
    public async Task NormalizesTheReason(string? reason, string expected)
    {
        var aggregator = VersionedAggregator();
        var hook = new DynatraceMetricsHook(aggregator, Clock829);

        await Run(hook, Details("flag", "on", reason));

        Assert.Equal(expected, aggregator.Snapshot()["flag"].Values.Single().Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task UsesDefaultSentinelWhenTheResolverReturnsNoVariant(string? variant)
    {
        var aggregator = VersionedAggregator();
        var hook = new DynatraceMetricsHook(aggregator, Clock829);

        await Run(hook, Details("flag", variant, Reason.Static));

        Assert.Equal("__default__", aggregator.Snapshot()["flag"].Values.Single().Variant);
    }

    [Theory]
    [InlineData(1741780820L, 1741780820L)]
    [InlineData(1741780829L, 1741780820L)]
    [InlineData(1741780830L, 1741780830L)]
    public async Task BucketsTimestampsInto10SecondEpochBuckets(long epochSec, long expectedBucket)
    {
        var aggregator = VersionedAggregator();
        var hook = new DynatraceMetricsHook(aggregator, () => DateTimeOffset.FromUnixTimeSeconds(epochSec));

        await Run(hook, Details("flag", "on", Reason.Static));

        Assert.Equal(expectedBucket, aggregator.Snapshot()["flag"].Values.Single().TimestampSec);
    }

    [Fact]
    public async Task PassesTheVersionFromFlagMetadataToTheAggregator()
    {
        // the provider sets the version first; the hook passes the metadata one through for validation
        var aggregator = VersionedAggregator();
        var hook = new DynatraceMetricsHook(aggregator, Clock823);
        var metadata = new ImmutableMetadata(new Dictionary<string, object> { ["version"] = "1741780800" });

        await Run(hook, Details("header-redesign", "control", Reason.TargetingMatch, metadata: metadata));

        Assert.Equal(1, aggregator.Size); // recorded: the version from the metadata matched the aggregator's
        Assert.Equal("1741780800", aggregator.Version);
    }

    [Fact]
    public async Task DropsEvaluationsWhoseMetadataVersionDiffersFromTheAggregators()
    {
        var aggregator = VersionedAggregator();
        var hook = new DynatraceMetricsHook(aggregator, Clock823);
        var metadata = new ImmutableMetadata(new Dictionary<string, object> { ["version"] = "some-other-version" });

        await Run(hook, Details("flag", "on", Reason.Static, metadata: metadata));

        Assert.Equal(0, aggregator.Size);
    }

    [Fact]
    public async Task IgnoresAMetadataVersionThatIsNotAString()
    {
        var aggregator = VersionedAggregator();
        var hook = new DynatraceMetricsHook(aggregator, Clock823);
        var metadata = new ImmutableMetadata(new Dictionary<string, object> { ["version"] = 42 });

        await Run(hook, Details("flag", "on", Reason.Static, metadata: metadata));

        Assert.Equal(1, aggregator.Size); // unreadable version means no check, not a crash and not a drop
    }

    [Fact]
    public async Task FallsBackToTheContextFlagKeyWhenTheDetailsHaveNone()
    {
        var aggregator = VersionedAggregator();
        var hook = new DynatraceMetricsHook(aggregator, Clock823);

        await hook.FinallyAsync(
            BooleanHookContext("from-context"),
            Details(string.Empty, "on", Reason.Static),
            new Dictionary<string, object>());

        Assert.True(aggregator.Snapshot().ContainsKey("from-context"));
    }

    [Fact]
    public async Task SkipsEvaluationsWithoutAnyFlagKey()
    {
        var aggregator = VersionedAggregator();
        var hook = new DynatraceMetricsHook(aggregator, Clock823);

        await hook.FinallyAsync(BooleanHookContext(" "), Details(" ", "on", Reason.Static), new Dictionary<string, object>());

        Assert.Equal(0, aggregator.Size);
    }

    [Fact]
    public async Task InvokesTheOnRecordCallbackAfterRecording()
    {
        var calls = 0;
        var hook = new DynatraceMetricsHook(VersionedAggregator(), Clock823, () => calls++);

        await Run(hook, Details("flag", "on", Reason.Static));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OnRecordCallbackThatThrows_NeverBreaksTheEvaluationPath()
    {
        var aggregator = VersionedAggregator();
        var hook = new DynatraceMetricsHook(aggregator, Clock823, () => throw new InvalidOperationException("flush bookkeeping failed"));

        await Run(hook, Details("flag", "on", Reason.Static)); // must not throw

        Assert.Equal(1, aggregator.Size);
    }
}
