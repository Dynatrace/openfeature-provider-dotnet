namespace Dynatrace.OpenFeature;

/// <summary>
/// One bucket of aggregated evaluations for a single (variant, reason, errorCode, 10s timestamp bucket) tuple.
/// </summary>
/// <remarks>
/// The dimension values are stored once in the <see cref="EvaluationMetricKey"/> that identifies this bucket; this
/// class holds only the mutable <see cref="Count"/>.
/// </remarks>
internal sealed class EvaluationMetricAggregation
{
    public EvaluationMetricAggregation(EvaluationMetricKey key)
    {
        Key = key;
    }

    public EvaluationMetricKey Key { get; }

    public string Variant => Key.Variant;

    public string Reason => Key.Reason;

    public string? ErrorCode => Key.ErrorCode;

    public long TimestampSec => Key.TimestampSec;

    public long Count { get; private set; }

    public void Increment() => Count++;

    public override string ToString() => $"EvaluationMetricAggregation{{Key={Key}, Count={Count}}}";
}
