using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenFeature;
using OpenFeature.Error;
using OpenFeature.Model;
using OpenFeature.Providers.Flagd.Core;

namespace Dynatrace.OpenFeature;

/// <summary>
/// Dynatrace OpenFeature provider for .NET (server-side).
/// </summary>
/// <remarks>
/// <para>
/// Wraps the flagd core engine for in-process evaluation. <see cref="InitializeAsync"/> fetches the flag
/// configuration from the Dynatrace CDN once and loads it into the engine, after which every evaluation is
/// local and makes no network call.
/// </para>
/// <para>
/// The configuration is not refreshed after initialization yet.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// await Api.Instance.SetProviderAsync(new DynatraceProvider("dt01.server_us_abcdef1234.de848e97a9cc4cc78aae568e65f49a9d_a1b2c3d4e5"));
/// </code>
/// </example>
public sealed class DynatraceProvider : FeatureProvider
{
    internal const string ProviderName = "dynatrace";

    /// <summary>SDK key format: <c>{token}.server_{geo}_{publicRandom}.{secret}_{sha1}</c>.</summary>
    internal static readonly Regex KeyPattern = new Regex(
        "^(?<token>dt[0-9]{2})\\.(?<type>server)_(?<geo>[a-z]{2,})_(?<publicRandom>[a-zA-Z0-9]{10,})\\.(?<secret>[a-fA-F0-9]{32,})_(?<sha>[a-f0-9]{10,})$",
        RegexOptions.CultureInvariant);

    private static readonly Metadata ProviderMetadata = new Metadata(ProviderName);

    // Shared across providers, as recommended for HttpClient; requests are bounded by CdnFetcher.RequestTimeout.
    private static readonly HttpClient SharedHttpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

    private readonly Uri _cdnUrl;
    private readonly FlagdCore _flagdCore;
    private readonly CdnFetcher _fetcher;
    private readonly ILogger? _logger;

    private int _shutdown;

    /// <summary>
    /// Creates a provider from the SDK key. The CDN URL is derived automatically from the key.
    /// </summary>
    /// <param name="sdkKey">The SDK key, <c>dt{NN}.server_{geo}_{rand}.{secret}_{sha}</c>.</param>
    /// <param name="options">Optional configuration (e.g. a config origin override).</param>
    /// <exception cref="ArgumentException"><paramref name="sdkKey"/> is null, blank or malformed.</exception>
    public DynatraceProvider(string sdkKey, DynatraceProviderOptions? options = null)
        : this(sdkKey, options, new FlagdCore(new FlagdCoreOptions { Logger = options?.Logger }), SharedHttpClient)
    {
    }

    // tests only: lets tests inject every collaborator individually
    internal DynatraceProvider(
        string sdkKey, DynatraceProviderOptions? options, FlagdCore flagdCore, HttpClient httpClient)
        : this(
            sdkKey,
            options,
            flagdCore,
            new CdnFetcher(BuildCdnUrl(sdkKey, options), httpClient, options?.Logger))
    {
    }

    // tests only: as above, with a pre-built CdnFetcher (e.g. one with a no-delay retry strategy)
    internal DynatraceProvider(
        string sdkKey, DynatraceProviderOptions? options, FlagdCore flagdCore, CdnFetcher fetcher)
    {
        var safeOptions = options ?? new DynatraceProviderOptions();

        _cdnUrl = BuildCdnUrl(sdkKey, safeOptions);
        _logger = safeOptions.Logger;
        _flagdCore = flagdCore;
        _fetcher = fetcher;
    }

    /// <summary>
    /// Builds the CDN URL of the SDK key's flag configuration: the config origin override when set, otherwise the
    /// production origin derived from the key's geo.
    /// </summary>
    internal static Uri BuildCdnUrl(string? sdkKey, DynatraceProviderOptions? options)
    {
        var match = ValidateSdkKey(sdkKey);
        var trimmed = match.Value;
        var type = match.Groups["type"].Value;
        var geo = match.Groups["geo"].Value;
        var overrideOrigin = options?.ConfigOriginUri;
        var origin = overrideOrigin is null
            ? $"https://cdn.{geo}.fm.dynatrace.com"
            : overrideOrigin.GetLeftPart(UriPartial.Authority);

        return new Uri($"{origin}/{type}/{trimmed}.json");
    }

    private static Match ValidateSdkKey(string? sdkKey)
    {
        if (sdkKey is null || string.IsNullOrWhiteSpace(sdkKey))
        {
            throw new ArgumentException("key must not be null or blank", nameof(sdkKey));
        }

        var trimmed = sdkKey.Trim();
        var match = KeyPattern.Match(trimmed);
        if (!match.Success)
        {
            // Only the public part may appear in the message; the secret must never be echoed.
            var lastDot = trimmed.LastIndexOf('.');
            var publicPart = lastDot > 0 ? trimmed.Substring(0, lastDot) : "<redacted>";
            throw new ArgumentException(
                $"invalid key (public: '{publicPart}'); expected dt{{NN}}.server_{{geo}}_{{rand}}.{{secret}}_{{sha}}",
                nameof(sdkKey));
        }

        return match;
    }

    /// <inheritdoc />
    public override Metadata GetMetadata() => ProviderMetadata;

    /// <summary>
    /// Fetches the initial flag configuration from the CDN and loads it into the evaluation engine. When this
    /// completes the SDK considers the provider ready; if it throws, the provider is in the error state.
    /// </summary>
    /// <remarks>
    /// Initialization order (per spec 3.1): fetch the flagd config JSON from the CDN, then load it into the
    /// engine. Opening a change-notification stream and starting the refresh timer are not implemented yet.
    /// </remarks>
    /// <exception cref="HttpRequestException">
    /// The initial fetch failed at the network level, or the CDN answered with a non-success status.
    /// </exception>
    /// <exception cref="TimeoutException">The initial fetch timed out.</exception>
    /// <exception cref="ParseErrorException">The response body was empty or is not a valid flag configuration.</exception>
    /// <exception cref="InvalidOperationException">The fetch was skipped, so no request was made.</exception>
    public override async Task InitializeAsync(EvaluationContext context, CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("initializing Dynatrace feature flag provider (host={Host})", _cdnUrl.Host);

        try
        {
            var result = await _fetcher.FetchAsync(cancellationToken).ConfigureAwait(false);
            if (result.IsSkipped)
            {
                throw new InvalidOperationException("initial CDN fetch was skipped; no request was made");
            }

            if (!result.IsSuccess)
            {
                throw new HttpRequestException($"initial CDN fetch returned HTTP {result.StatusCode}");
            }

            var body = result.Body;
            if (body is null || string.IsNullOrWhiteSpace(body))
            {
                throw new ParseErrorException("initial CDN response body was empty");
            }

            await _flagdCore.InitializeAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                _flagdCore.SetConfigurations(body);
            }
            catch (Exception e) when (!(e is OperationCanceledException || e is ParseErrorException))
            {
                // CDN content is untrusted input: the engine documents ParseErrorException, but schema validation
                // can surface parser exceptions of its own. Either way it is a configuration problem.
                throw new ParseErrorException($"initial CDN config is invalid: {e.Message}", e);
            }
        }
        catch (Exception e)
        {
            // The validators were cached when the 2xx arrived, before the body was rejected; drop them so a later
            // fetch cannot "recover" with a 304 for a config that was never loaded.
            _fetcher.InvalidateCache();
            _logger?.LogError(e, "initialization failed");
            throw;
        }

        _logger?.LogInformation("Dynatrace provider initialized successfully");
    }

    /// <summary>
    /// Shuts the provider down. Idempotent.
    /// </summary>
    public override Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _shutdown, 1) == 1)
        {
            return Task.CompletedTask;
        }

        _logger?.LogInformation("shutting down Dynatrace provider");
        _logger?.LogInformation("Dynatrace provider shut down");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task<ResolutionDetails<bool>> ResolveBooleanValueAsync(
        string flagKey, bool defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default)
        => Task.FromResult(_flagdCore.ResolveBoolean(flagKey, defaultValue, context));

    /// <inheritdoc />
    public override Task<ResolutionDetails<string>> ResolveStringValueAsync(
        string flagKey, string defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default)
        => Task.FromResult(_flagdCore.ResolveString(flagKey, defaultValue, context));

    /// <inheritdoc />
    public override Task<ResolutionDetails<int>> ResolveIntegerValueAsync(
        string flagKey, int defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default)
        => Task.FromResult(_flagdCore.ResolveInteger(flagKey, defaultValue, context));

    /// <inheritdoc />
    public override Task<ResolutionDetails<double>> ResolveDoubleValueAsync(
        string flagKey, double defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default)
        => Task.FromResult(_flagdCore.ResolveDouble(flagKey, defaultValue, context));

    /// <inheritdoc />
    public override Task<ResolutionDetails<Value>> ResolveStructureValueAsync(
        string flagKey, Value defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default)
        => Task.FromResult(_flagdCore.ResolveStructure(flagKey, defaultValue, context));
}
