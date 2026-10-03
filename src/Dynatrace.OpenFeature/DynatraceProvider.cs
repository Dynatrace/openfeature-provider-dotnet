using System;
using System.Threading;
using System.Threading.Tasks;
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Model;

namespace Dynatrace.OpenFeature;

/// <summary>
/// Dynatrace OpenFeature provider for .NET (server-side).
/// </summary>
/// <remarks>
/// Scaffolding only: flag evaluation is not implemented yet. Every resolution reports
/// <see cref="ErrorType.ProviderNotReady"/> and returns the caller's default value.
/// </remarks>
public sealed class DynatraceProvider : FeatureProvider
{
    internal const string ProviderName = "dynatrace";

    private const string NotImplementedMessage = "DynatraceProvider flag evaluation is not implemented yet.";

    private readonly string _sdkKey;

    /// <summary>
    /// Creates a provider for the given Dynatrace server SDK key.
    /// </summary>
    /// <param name="sdkKey">The Dynatrace server SDK key (<c>dt01.server_...</c>).</param>
    /// <exception cref="ArgumentException"><paramref name="sdkKey"/> is null, empty or whitespace.</exception>
    public DynatraceProvider(string sdkKey)
    {
        if (string.IsNullOrWhiteSpace(sdkKey))
        {
            throw new ArgumentException("sdkKey must not be null or blank", nameof(sdkKey));
        }

        _sdkKey = sdkKey.Trim();
    }

    /// <inheritdoc />
    public override Metadata GetMetadata() => new Metadata(ProviderName);

    /// <inheritdoc />
    public override Task<ResolutionDetails<bool>> ResolveBooleanValueAsync(
        string flagKey, bool defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default)
        => NotReady(flagKey, defaultValue);

    /// <inheritdoc />
    public override Task<ResolutionDetails<string>> ResolveStringValueAsync(
        string flagKey, string defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default)
        => NotReady(flagKey, defaultValue);

    /// <inheritdoc />
    public override Task<ResolutionDetails<int>> ResolveIntegerValueAsync(
        string flagKey, int defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default)
        => NotReady(flagKey, defaultValue);

    /// <inheritdoc />
    public override Task<ResolutionDetails<double>> ResolveDoubleValueAsync(
        string flagKey, double defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default)
        => NotReady(flagKey, defaultValue);

    /// <inheritdoc />
    public override Task<ResolutionDetails<Value>> ResolveStructureValueAsync(
        string flagKey, Value defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default)
        => NotReady(flagKey, defaultValue);

    private static Task<ResolutionDetails<T>> NotReady<T>(string flagKey, T defaultValue)
        => Task.FromResult(new ResolutionDetails<T>(
            flagKey,
            defaultValue,
            ErrorType.ProviderNotReady,
            Reason.Error,
            errorMessage: NotImplementedMessage));
}
