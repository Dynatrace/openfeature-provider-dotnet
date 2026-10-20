using System;
using Microsoft.Extensions.Logging;

namespace Dynatrace.OpenFeature;

/// <summary>
/// Optional configuration for <see cref="DynatraceProvider"/>.
/// </summary>
public sealed class DynatraceProviderOptions
{
    internal static readonly TimeSpan DefaultMetricsFlushInterval = TimeSpan.FromSeconds(30);

    // distinct flag keys that trigger an out-of-band flush
    internal const int DefaultMetricsSoftCapDistinctFlags = 1_000;

    // distinct flag keys past which new flags are dropped
    internal const int DefaultMetricsHardCapDistinctFlags = 2_000;

    private Uri? _configOriginUri;
    private Uri? _metricsOriginUri;

    /// <summary>
    /// Overrides the flag configuration origin: scheme, host and optional port, with no path, query,
    /// fragment or user info (e.g. <c>https://cdn.eu.fm.dynatracelabs.com</c>). For dev/labs environments
    /// or a proxy; the production origin is derived from the SDK key when this is <c>null</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The value is blank or not a bare http(s) origin.</exception>
    public string? ConfigOrigin
    {
        get => _configOriginUri?.GetLeftPart(UriPartial.Authority);
        set => _configOriginUri = value is null ? null : ParseOrigin(value, nameof(ConfigOrigin));
    }

    /// <summary>
    /// Overrides the evaluation metrics origin: scheme, host and optional port only (e.g.
    /// <c>https://metrics.eu.fm.dynatracelabs.com</c>); <c>/v1/metrics</c> is appended. Derived from the SDK key when
    /// <c>null</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The value is blank or not a bare http(s) origin.</exception>
    public string? MetricsOrigin
    {
        get => _metricsOriginUri?.GetLeftPart(UriPartial.Authority);
        set => _metricsOriginUri = value is null ? null : ParseOrigin(value, nameof(MetricsOrigin));
    }

    /// <summary>How often evaluation metrics are sent. Defaults to 30 seconds; non-positive values fall back to it.</summary>
    public TimeSpan MetricsFlushInterval { get; set; } = DefaultMetricsFlushInterval;

    /// <summary>
    /// Logger for provider lifecycle messages and flag configuration schema warnings. When <c>null</c>,
    /// nothing is logged.
    /// </summary>
    public ILogger? Logger { get; set; }

    /// <summary>The validated, canonical config origin override, or <c>null</c> to derive it from the SDK key.</summary>
    internal Uri? ConfigOriginUri => _configOriginUri;

    /// <summary>The validated, canonical metrics origin override, or <c>null</c> to derive it from the SDK key.</summary>
    internal Uri? MetricsOriginUri => _metricsOriginUri;

    /// <summary>
    /// Validates <paramref name="value"/> as a bare origin and returns it in canonical form.
    /// </summary>
    /// <remarks>
    /// Rebuilding from the parsed parts (rather than trimming the raw string) keeps a query or fragment
    /// from silently corrupting the appended path, which would surface only as a 404 on the first request.
    /// </remarks>
    private static Uri ParseOrigin(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{fieldName} must not be null or blank", fieldName);
        }

        var stripped = value.Trim();
        if (!Uri.TryCreate(stripped, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"{fieldName} is not a valid URI; got: \"{stripped}\"", fieldName);
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException($"{fieldName} must use http or https; got: \"{stripped}\"", fieldName);
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException($"{fieldName} must not include user info; got: \"{stripped}\"", fieldName);
        }

        // Checked on the raw string: Uri silently normalises paths such as "/.." and "//" away.
        var afterScheme = stripped.Substring(stripped.IndexOf("://", StringComparison.Ordinal) + 3);
        var restIndex = afterScheme.IndexOfAny(new[] { '/', '?', '#' });
        var rest = restIndex < 0 ? string.Empty : afterScheme.Substring(restIndex);
        if (rest.Length > 0 && rest != "/")
        {
            var component = rest[0] == '?' || rest.StartsWith("/?", StringComparison.Ordinal)
                ? "a query string"
                : rest[0] == '#' || rest.StartsWith("/#", StringComparison.Ordinal)
                    ? "a fragment"
                    : "a path";
            throw new ArgumentException($"{fieldName} must not include {component}; got: \"{stripped}\"", fieldName);
        }

        return new Uri(uri.GetLeftPart(UriPartial.Authority));
    }
}
