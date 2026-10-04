using System;

namespace Dynatrace.OpenFeature;

/// <summary>
/// Identifies one bucket of aggregated evaluations for a single flag: the (variant, reason, errorCode, 10s timestamp
/// bucket) tuple. A typed key rather than a concatenated string, so values that contain a delimiter cannot collide.
/// </summary>
internal readonly struct EvaluationMetricKey : IEquatable<EvaluationMetricKey>
{
    public EvaluationMetricKey(string variant, string reason, string? errorCode, long timestampSec)
    {
        Variant = variant;
        Reason = reason;
        ErrorCode = errorCode;
        TimestampSec = timestampSec;
    }

    public string Variant { get; }

    public string Reason { get; }

    public string? ErrorCode { get; }

    public long TimestampSec { get; }

    public bool Equals(EvaluationMetricKey other)
        => TimestampSec == other.TimestampSec
           && string.Equals(Variant, other.Variant, StringComparison.Ordinal)
           && string.Equals(Reason, other.Reason, StringComparison.Ordinal)
           && string.Equals(ErrorCode, other.ErrorCode, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is EvaluationMetricKey other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            hash = (hash * 31) + (Variant?.GetHashCode() ?? 0);
            hash = (hash * 31) + (Reason?.GetHashCode() ?? 0);
            hash = (hash * 31) + (ErrorCode?.GetHashCode() ?? 0);
            hash = (hash * 31) + TimestampSec.GetHashCode();
            return hash;
        }
    }

    public override string ToString()
        => $"EvaluationMetricKey{{Variant='{Variant}', Reason='{Reason}', ErrorCode='{ErrorCode}', TimestampSec={TimestampSec}}}";
}
