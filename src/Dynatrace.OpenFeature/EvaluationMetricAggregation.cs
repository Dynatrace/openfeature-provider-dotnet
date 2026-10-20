namespace Dynatrace.OpenFeature;

/// <summary>The mutable count for one <see cref="EvaluationMetricKey"/>.</summary>
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
