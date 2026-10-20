using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Model;

namespace Dynatrace.OpenFeature;

/// <summary>Records every evaluation into the metrics aggregator, normalized per the evaluation-metrics spec.</summary>
internal sealed class DynatraceMetricsHook : Hook
{
    internal const string DefaultVariant = "__default__";
    internal const string DefaultReason = "default";
    internal const string UnknownReason = "unknown";
    internal const string ErrorReason = "error";
    internal const string VersionMetadataKey = "version";

    // the SDK's spec codes (e.g. FLAG_NOT_FOUND); its GetDescription helper is internal
    private static readonly Dictionary<ErrorType, string> ErrorCodeNames = Enum.GetValues(typeof(ErrorType))
        .Cast<ErrorType>()
        .ToDictionary(
            e => e,
            e => (typeof(ErrorType).GetField(e.ToString())?.GetCustomAttribute<DescriptionAttribute>()?.Description
                  ?? e.ToString()).ToLowerInvariant());

    private readonly DynatraceMetricsAggregator _aggregator;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action _onRecord;
    private readonly ILogger? _logger;

    public DynatraceMetricsHook(DynatraceMetricsAggregator aggregator, ILogger? logger = null)
        : this(aggregator, () => DateTimeOffset.UtcNow, null, logger)
    {
    }

    // visible for testing
    public DynatraceMetricsHook(
        DynatraceMetricsAggregator aggregator, Func<DateTimeOffset> clock, Action? onRecord = null, ILogger? logger = null)
    {
        _aggregator = aggregator;
        _clock = clock;
        _onRecord = onRecord ?? (() => { });
        _logger = logger;
    }

    /// <inheritdoc />
    public override ValueTask FinallyAsync<T>(
        HookContext<T> context,
        FlagEvaluationDetails<T> evaluationDetails,
        IReadOnlyDictionary<string, object>? hints = null,
        CancellationToken cancellationToken = default)
    {
        var flagKey = !string.IsNullOrWhiteSpace(evaluationDetails.FlagKey) ? evaluationDetails.FlagKey : context.FlagKey;
        if (string.IsNullOrWhiteSpace(flagKey))
        {
            return default;
        }

        var rawReason = evaluationDetails.Reason;
        var errorCode = NormalizeErrorCode(evaluationDetails.ErrorType);
        _aggregator.Record(
            flagKey,
            NormalizeVariant(evaluationDetails.Variant, rawReason, errorCode),
            NormalizeReason(rawReason, errorCode),
            errorCode,
            CurrentTimestampBucketSec(),
            ReadVersion(evaluationDetails));

        try
        {
            _onRecord();
        }
        catch (Exception e)
        {
            // never block the evaluation path
            _logger?.LogWarning(e, "metrics hook onRecord callback failed");
        }

        return default;
    }

    private static string NormalizeVariant(string? variant, string? reason, string? errorCode)
    {
        if (errorCode is not null || IsDefaultReason(reason) || variant is null || string.IsNullOrWhiteSpace(variant))
        {
            return DefaultVariant;
        }

        return variant;
    }

    private static string NormalizeReason(string? reason, string? errorCode)
    {
        if (errorCode is not null)
        {
            return ErrorReason;
        }

        if (reason is null || string.IsNullOrWhiteSpace(reason))
        {
            return UnknownReason;
        }

        return reason.ToLowerInvariant();
    }

    private static bool IsDefaultReason(string? reason)
        => string.Equals(reason, Reason.Default, StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeErrorCode(ErrorType errorType)
    {
        if (errorType == ErrorType.None)
        {
            return null;
        }

        return ErrorCodeNames.TryGetValue(errorType, out var name) ? name : errorType.ToString().ToLowerInvariant();
    }

    private static string? ReadVersion<T>(FlagEvaluationDetails<T> details)
    {
        var metadata = details.FlagMetadata;
        return metadata is null ? null : metadata.GetString(VersionMetadataKey);
    }

    /// <summary>The current time truncated to its 10-second epoch bucket.</summary>
    private long CurrentTimestampBucketSec()
    {
        var epochSec = _clock().ToUnixTimeSeconds();
        return FloorDiv(epochSec, 10) * 10;
    }

    private static long FloorDiv(long value, long divisor)
    {
        var quotient = value / divisor;
        return (value % divisor != 0 && (value < 0) != (divisor < 0)) ? quotient - 1 : quotient;
    }
}
