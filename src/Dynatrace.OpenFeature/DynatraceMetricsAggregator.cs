using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Dynatrace.OpenFeature;

/// <summary>Two-level aggregate map: <c>map[flagKey][variant, reason, errorCode, timestampSec]</c>.</summary>
internal sealed class DynatraceMetricsAggregator
{
    private readonly object _lock = new object();
    private readonly int _maxDistinctFlags;
    private readonly ILogger? _logger;

    private Dictionary<string, Dictionary<EvaluationMetricKey, EvaluationMetricAggregation>> _aggregateMetrics =
        new Dictionary<string, Dictionary<EvaluationMetricKey, EvaluationMetricAggregation>>();

    private string? _version;

    public DynatraceMetricsAggregator(
        int maxDistinctFlags = DynatraceProviderOptions.DefaultMetricsHardCapDistinctFlags, ILogger? logger = null)
    {
        if (maxDistinctFlags <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDistinctFlags), "maxDistinctFlags must be positive");
        }

        _maxDistinctFlags = maxDistinctFlags;
        _logger = logger;
    }

    /// <summary>The latest config version observed in flag-evaluation metadata, or <c>null</c> if none yet.</summary>
    public string? Version
    {
        get
        {
            lock (_lock)
            {
                return _version;
            }
        }
    }

    /// <summary>Number of distinct flag keys currently in the live map.</summary>
    public int Size
    {
        get
        {
            lock (_lock)
            {
                return _aggregateMetrics.Count;
            }
        }
    }

    /// <summary>Records one evaluation; dropped with no version, a version mismatch, or a new flag over the cap.</summary>
    public void Record(
        string flagKey, string variant, string reason, string? errorCode, long timestampSec, string? evaluationVersion = null)
    {
        lock (_lock)
        {
            if (_version is null)
            {
                _logger?.LogDebug("dropping evaluation for '{FlagKey}': no config version observed yet", flagKey);
                return;
            }

            if (evaluationVersion is not null && _version != evaluationVersion)
            {
                return;
            }

            if (!_aggregateMetrics.TryGetValue(flagKey, out var flagEvaluations))
            {
                if (_aggregateMetrics.Count >= _maxDistinctFlags)
                {
                    _logger?.LogDebug("Dropping metrics due to max aggregator size");
                    return;
                }

                flagEvaluations = new Dictionary<EvaluationMetricKey, EvaluationMetricAggregation>();
                _aggregateMetrics[flagKey] = flagEvaluations;
            }

            var key = new EvaluationMetricKey(variant, reason, errorCode, timestampSec);
            if (!flagEvaluations.TryGetValue(key, out var aggregation))
            {
                aggregation = new EvaluationMetricAggregation(key);
                flagEvaluations[key] = aggregation;
            }

            aggregation.Increment();
        }
    }

    /// <summary>Records the latest config version observed in flag-evaluation metadata. Blank values are ignored.</summary>
    public void SetVersion(string? version)
    {
        if (version is null || string.IsNullOrWhiteSpace(version))
        {
            return;
        }

        lock (_lock)
        {
            _version = version;
        }
    }

    /// <summary>Swaps out the live map and returns it with its version.</summary>
    public DrainedBatch Drain()
    {
        lock (_lock)
        {
            var previous = _aggregateMetrics;
            _aggregateMetrics = new Dictionary<string, Dictionary<EvaluationMetricKey, EvaluationMetricAggregation>>();
            return new DrainedBatch(previous, _version);
        }
    }

    /// <summary>For testing only. The production flush path uses <see cref="Drain"/>.</summary>
    internal IReadOnlyDictionary<string, IReadOnlyDictionary<EvaluationMetricKey, EvaluationMetricAggregation>> Snapshot()
    {
        lock (_lock)
        {
            return _aggregateMetrics.ToDictionary(
                outer => outer.Key,
                outer => (IReadOnlyDictionary<EvaluationMetricKey, EvaluationMetricAggregation>)
                    new Dictionary<EvaluationMetricKey, EvaluationMetricAggregation>(outer.Value));
        }
    }

    /// <summary>A drained map and the config version it was recorded under.</summary>
    internal sealed class DrainedBatch
    {
        private readonly Dictionary<string, Dictionary<EvaluationMetricKey, EvaluationMetricAggregation>> _evaluations;

        internal DrainedBatch(
            Dictionary<string, Dictionary<EvaluationMetricKey, EvaluationMetricAggregation>> evaluations, string? version)
        {
            _evaluations = evaluations;
            Version = version;
        }

        public string? Version { get; }

        public bool IsEmpty => _evaluations.Count == 0;

        /// <summary>The drained two-level evaluations map, keyed by flag key.</summary>
        public IReadOnlyDictionary<string, IReadOnlyDictionary<EvaluationMetricKey, EvaluationMetricAggregation>> Evaluations
            => _evaluations.ToDictionary(
                outer => outer.Key,
                outer => (IReadOnlyDictionary<EvaluationMetricKey, EvaluationMetricAggregation>)outer.Value);
    }
}
