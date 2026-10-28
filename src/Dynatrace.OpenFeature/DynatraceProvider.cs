using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Net.Http;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenFeature;
using OpenFeature.Constant;
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
/// configuration from the Dynatrace CDN and loads it into the engine, after which every evaluation is local and
/// makes no network call. The configuration is then kept up to date by polling the CDN (with ETag revalidation, so an
/// unchanged configuration costs a 304), and by re-fetching whenever the SSE change-notification stream the
/// configuration advertises announces a change. Polling runs every 10 seconds, relaxing to every 10 minutes while
/// the stream is connected and fetches succeed.
/// </para>
/// <para>
/// Evaluations are aggregated in memory by a provider hook and sent to the Dynatrace metrics endpoint in the
/// background.
/// </para>
/// <para>
/// A failed poll moves the provider to <c>Stale</c>; if the CDN stays unreachable for the one-minute grace period it
/// moves to <c>Error</c>, and the next successful poll recovers it to <c>Ready</c>. A failed initial fetch recovers the
/// same way. Changes to the flag configuration are announced as configuration-changed events.
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

    /// <summary>
    /// Poll interval while no change-notification stream is connected, or after a failed fetch: aggressive, to pick
    /// up changes and recover quickly.
    /// </summary>
    internal static readonly TimeSpan PollIntervalSseDisconnected = TimeSpan.FromSeconds(10);

    /// <summary>Poll interval while the stream is connected and fetches succeed: a safety net.</summary>
    internal static readonly TimeSpan PollIntervalSseConnected = TimeSpan.FromMinutes(10);

    /// <summary>How long the provider stays <c>Stale</c> before escalating to <c>Error</c>.</summary>
    internal static readonly TimeSpan GracePeriod = TimeSpan.FromMinutes(1);

    private static readonly Metadata ProviderMetadata = new Metadata(ProviderName);

    // Shared across providers, as recommended for HttpClient; requests are bounded by CdnFetcher.RequestTimeout.
    private static readonly HttpClient SharedHttpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

    private readonly Uri _cdnUrl;
    private readonly FlagdCore _flagdCore;
    private readonly CdnFetcher _fetcher;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _pollIntervalSseConnected;
    private readonly TimeSpan _gracePeriod;
    private readonly bool _sseEnabled;
    private readonly DynatraceMetricsAggregator _metricsAggregator;
    private readonly EvaluationMetricsFlusher _metricsFlusher;
    private readonly IImmutableList<Hook> _providerHooks;
    private readonly ILogger? _logger;

    // guards state transitions; events are enqueued under it so they keep state-change order
    private readonly object _stateLock = new object();

    // unbounded outbox drained by the pump; the SDK channel is bounded and could block a transition
    private readonly Channel<ProviderEventPayload> _outbox = Channel.CreateUnbounded<ProviderEventPayload>(
        new UnboundedChannelOptions { SingleReader = true });

    // Cancelled on shutdown: ends the event pump and the grace timer.
    private readonly CancellationTokenSource _shutdownCts = new CancellationTokenSource();

    private volatile ProviderStatus _state = ProviderStatus.NotReady;
    private int _shutdown;
    private int _eventPumpStarted;
    private PollingManager? _pollingManager;

    // grace timer: transitions Stale -> Error after the grace period
    private CancellationTokenSource? _graceCts;

    // guarded by _stateLock, together with the cadence they select
    private SseManager? _sseManager;
    private bool _sseConnected;

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
            new CdnFetcher(BuildCdnUrl(sdkKey, options), httpClient, options?.Logger),
            httpClient,
            PollIntervalSseDisconnected,
            PollIntervalSseConnected,
            GracePeriod)
    {
    }

    // tests only: production wiring with a shortened SSE-connected cadence and grace period
    internal DynatraceProvider(
        string sdkKey, DynatraceProviderOptions? options, TimeSpan pollIntervalSseConnected, TimeSpan gracePeriod)
        : this(
            sdkKey,
            options,
            new FlagdCore(new FlagdCoreOptions { Logger = options?.Logger }),
            new CdnFetcher(BuildCdnUrl(sdkKey, options), SharedHttpClient, options?.Logger),
            SharedHttpClient,
            PollIntervalSseDisconnected,
            pollIntervalSseConnected,
            gracePeriod)
    {
    }

    // tests only: as above, with a pre-built CdnFetcher (e.g. one with a no-delay retry strategy)
    internal DynatraceProvider(
        string sdkKey, DynatraceProviderOptions? options, FlagdCore flagdCore, CdnFetcher fetcher)
        : this(sdkKey, options, flagdCore, fetcher, PollIntervalSseDisconnected, GracePeriod)
    {
    }

    // tests only: as above, plus timing controls
    internal DynatraceProvider(
        string sdkKey,
        DynatraceProviderOptions? options,
        FlagdCore flagdCore,
        CdnFetcher fetcher,
        TimeSpan pollInterval,
        TimeSpan gracePeriod)
        : this(sdkKey, options, flagdCore, fetcher, SharedHttpClient, pollInterval, gracePeriod)
    {
    }

    // tests only: as above, plus the metrics HTTP stack
    internal DynatraceProvider(
        string sdkKey,
        DynatraceProviderOptions? options,
        FlagdCore flagdCore,
        CdnFetcher fetcher,
        HttpClient metricsHttpClient,
        TimeSpan pollInterval,
        TimeSpan gracePeriod)
        : this(sdkKey, options, flagdCore, fetcher, metricsHttpClient, pollInterval, PollIntervalSseConnected, gracePeriod)
    {
    }

    // tests only: as above, plus the SSE-connected cadence
    internal DynatraceProvider(
        string sdkKey,
        DynatraceProviderOptions? options,
        FlagdCore flagdCore,
        CdnFetcher fetcher,
        HttpClient metricsHttpClient,
        TimeSpan pollInterval,
        TimeSpan pollIntervalSseConnected,
        TimeSpan gracePeriod)
    {
        var trimmedSdkKey = ValidateSdkKey(sdkKey).Value;
        var safeOptions = options ?? new DynatraceProviderOptions();

        _cdnUrl = BuildCdnUrl(sdkKey, safeOptions);
        _logger = safeOptions.Logger;
        _flagdCore = flagdCore;
        _fetcher = fetcher;
        _pollInterval = pollInterval;
        _pollIntervalSseConnected = pollIntervalSseConnected;
        _gracePeriod = gracePeriod;
        _sseEnabled = safeOptions.SseEnabled;
        _metricsAggregator = new DynatraceMetricsAggregator(logger: _logger);
        _metricsFlusher = new EvaluationMetricsFlusher(
            _metricsAggregator,
            metricsHttpClient,
            trimmedSdkKey,
            BuildMetricsUrl(sdkKey, safeOptions),
            ResolveMetricsFlushInterval(safeOptions.MetricsFlushInterval, _logger),
            DynatraceProviderOptions.DefaultMetricsSoftCapDistinctFlags,
            ResolveSdkVersion(),
            ProviderName,
            _logger);
        _providerHooks = ImmutableList.Create<Hook>(
            new DynatraceMetricsHook(_metricsAggregator, () => DateTimeOffset.UtcNow, _metricsFlusher.OnRecord, _logger));
    }

    /// <summary>The provider's own view of its lifecycle state, which it announces to the SDK as events.</summary>
    internal ProviderStatus State => _state;

    /// <summary>The poll/fetch coordinator, or <c>null</c> before <see cref="InitializeAsync"/> has run.</summary>
    internal PollingManager? PollingManager => _pollingManager;

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

    /// <summary>The metrics origin override plus <c>/v1/metrics</c>, else the endpoint for the key's geo.</summary>
    internal static Uri BuildMetricsUrl(string? sdkKey, DynatraceProviderOptions? options)
    {
        var overrideOrigin = options?.MetricsOriginUri;
        if (overrideOrigin is not null)
        {
            return new Uri($"{overrideOrigin.GetLeftPart(UriPartial.Authority)}/v1/metrics");
        }

        var geo = ValidateSdkKey(sdkKey).Groups["geo"].Value;
        return new Uri($"https://metrics.{geo}.fm.dynatrace.com/v1/metrics");
    }

    internal static TimeSpan ResolveMetricsFlushInterval(TimeSpan interval, ILogger? logger)
    {
        if (interval <= TimeSpan.Zero || interval.TotalMilliseconds < 1)
        {
            logger?.LogWarning("MetricsFlushInterval is invalid, using default value");
            return DynatraceProviderOptions.DefaultMetricsFlushInterval;
        }

        return interval;
    }

    /// <summary>The package version, without any source-revision suffix.</summary>
    private static string ResolveSdkVersion()
    {
        var assembly = typeof(DynatraceProvider).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString();
        if (version is null || string.IsNullOrWhiteSpace(version))
        {
            return "0.0.0";
        }

        var plus = version.IndexOf('+');
        return plus >= 0 ? version.Substring(0, plus) : version;
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

    /// <inheritdoc />
    public override IImmutableList<Hook> GetProviderHooks() => _providerHooks;

    /// <summary>
    /// Fetches the initial flag configuration from the CDN, loads it into the evaluation engine and starts
    /// polling for changes. When this completes the SDK considers the provider ready; if it throws, the provider is
    /// in the error state and recovers on its own once a poll succeeds.
    /// </summary>
    /// <remarks>
    /// Initialization order (per spec 3.1): fetch the flagd config JSON from the CDN, load it into the engine, then
    /// start the poll timer, only once the initial fetch has completed, so it never contends with the first tick
    /// (spec 2.1.1), then open the SSE stream the config advertises. A failed initial fetch still starts the timer before rethrowing, so it recovers like any other
    /// failure (spec 2.4); otherwise a transient 5xx at startup would strand the provider in the error state with
    /// nothing scheduled to retry.
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
        StartEventPump();

        Exception? initFailure = null;
        string? initialConfig = null;
        try
        {
            initialConfig = await LoadInitialConfigurationAsync(cancellationToken).ConfigureAwait(false);
            SetState(ProviderStatus.Ready);
        }
        catch (OperationCanceledException)
        {
            // The caller gave up; nothing to recover, so no polling.
            throw;
        }
        catch (Exception e)
        {
            // rethrown after polling starts; uncommitted validators make the next poll a full fetch
            initFailure = e;
            SetState(ProviderStatus.Error);
            _logger?.LogError(
                e, "initialization failed; starting polling-only recovery at {PollInterval}", _pollInterval);
        }

        // Started only now, after the initial fetch has completed. Reached on the failure path too.
        StartPolling();
        _metricsFlusher.Start();

        if (initialConfig is not null)
        {
            SyncSse(initialConfig);
        }

        if (initFailure is not null)
        {
            ExceptionDispatchInfo.Capture(initFailure).Throw();
        }

        _logger?.LogInformation("Dynatrace provider initialized successfully");
    }

    private async Task<string> LoadInitialConfigurationAsync(CancellationToken cancellationToken)
    {
        // Loads the config JSON schemas, so that the initial config and every polled one is validated.
        await _flagdCore.InitializeAsync(cancellationToken).ConfigureAwait(false);

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

        try
        {
            _flagdCore.SetConfigurations(body);
            UpdateMetricsVersionFromFlagSet();
        }
        catch (Exception e) when (!(e is OperationCanceledException || e is ParseErrorException))
        {
            // untrusted CDN content; schema validation can throw parser exceptions besides ParseErrorException
            throw new ParseErrorException($"initial CDN config is invalid: {e.Message}", e);
        }

        _fetcher.Commit();
        return body;
    }

    private void StartPolling()
    {
        if (Volatile.Read(ref _shutdown) == 1)
        {
            return;
        }

        var pollingManager = new PollingManager(
            _fetcher.FetchAsync,
            StoreFlags,
            _fetcher.Commit,
            _fetcher.IsNewerThanCommitted,
            OnConfigurationChanged,
            OnFetchSuccess,
            OnFetchFailure,
            _pollInterval,
            _logger);
        _pollingManager = pollingManager;
        pollingManager.Start();
    }

    /// <summary>
    /// Shuts the provider down: stops polling, cancels the grace timer and any in-flight fetch. Idempotent.
    /// </summary>
    public override async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _shutdown, 1) == 1)
        {
            return;
        }

        _logger?.LogInformation("shutting down Dynatrace provider");

        lock (_stateLock)
        {
            StopSse();
        }

        _pollingManager?.Stop();
        _shutdownCts.Cancel();

        try
        {
            await _metricsFlusher.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (!(e is OperationCanceledException))
        {
            _logger?.LogWarning(e, "metrics flusher shutdown failed");
        }

        // Taken so that a callback already past its shutdown check cannot move the state after we reset it.
        lock (_stateLock)
        {
            CancelGraceTimer();
            _state = ProviderStatus.NotReady;
        }

        _logger?.LogInformation("Dynatrace provider shut down");
    }

    // -- poll callbacks --

    // internal to let tests drive a config switch without standing up a real PollingManager
    internal IReadOnlyList<string> StoreFlags(string json)
    {
        if (Volatile.Read(ref _shutdown) == 1)
        {
            throw new InvalidOperationException("StoreFlags called after provider shutdown; ignoring new config");
        }

        // seal the outgoing version's evaluations before switching
        _metricsFlusher.EnqueueSealedBatch(_metricsAggregator.Drain());
        _metricsFlusher.TriggerFlush();

        try
        {
            var changed = _flagdCore.SetConfigurations(json);
            UpdateMetricsVersionFromFlagSet();
            SyncSse(json);
            return changed;
        }
        catch (Exception e) when (!(e is OperationCanceledException))
        {
            throw new ParseErrorException($"failed to store flag configuration: {e.Message}", e);
        }
    }

    private void UpdateMetricsVersionFromFlagSet()
    {
        if (_flagdCore.GetFlagSetMetadata().TryGetValue("version", out var version) && version is string versionText)
        {
            _metricsAggregator.SetVersion(versionText);
        }
    }

    private void OnFetchSuccess()
    {
        lock (_stateLock)
        {
            if (_sseConnected)
            {
                _pollingManager?.Interval = _pollIntervalSseConnected;
            }

            CancelGraceTimer();
            var current = _state;
            if (Volatile.Read(ref _shutdown) == 0 && (current == ProviderStatus.Stale || current == ProviderStatus.Error))
            {
                _state = ProviderStatus.Ready;
                _logger?.LogInformation("recovered from {State} to Ready", current);
                Emit(ProviderEventTypes.ProviderReady, "recovered");
            }
        }
    }

    /// <summary>
    /// Handles a poll failure: a ready provider goes stale and the grace timer starts. Any other state is already
    /// degraded, so the failure is only logged.
    /// </summary>
    private void OnFetchFailure(string reason)
    {
        lock (_stateLock)
        {
            // fetch at the fast cadence until one succeeds, even with SSE connected
            _pollingManager?.Interval = _pollInterval;
            if (Volatile.Read(ref _shutdown) == 0 && _state == ProviderStatus.Ready)
            {
                _state = ProviderStatus.Stale;
                _logger?.LogWarning("poll failed, transitioning to Stale: {Reason}", reason);
                Emit(ProviderEventTypes.ProviderStale, reason);
                StartGraceTimer();
            }
            else
            {
                _logger?.LogWarning("poll failed (state={State}): {Reason}", _state, reason);
            }
        }
    }

    private void OnConfigurationChanged(IReadOnlyList<string> changedKeys)
        => Emit(ProviderEventTypes.ProviderConfigurationChanged, flagsChanged: changedKeys);

    // -- SSE --

    /// <summary>Follows the stream the stored config advertises; the URL can rotate (KB section 1.5).</summary>
    private void SyncSse(string configJson)
    {
        if (!_sseEnabled)
        {
            return;
        }

        var url = CdnConfigJson.ExtractSseUrl(configJson, _cdnUrl);
        lock (_stateLock)
        {
            if (Volatile.Read(ref _shutdown) == 1)
            {
                return;
            }

            if (url is null)
            {
                if (_sseManager is not null)
                {
                    _logger?.LogInformation("config no longer advertises an SSE stream; falling back to polling only");
                    StopSse();
                }

                return;
            }

            if (_sseManager?.Url.AbsoluteUri == url.AbsoluteUri)
            {
                return;
            }

            if (_sseManager is not null)
            {
                _logger?.LogInformation("SSE stream URL rotated; reconnecting");
                StopSse();
            }

            var manager = new SseManager(url, OnSseConnected, OnSseDisconnected, OnSseMessage, _logger);
            _sseManager = manager;
            manager.Start();
        }
    }

    // Caller holds _stateLock.
    private void StopSse()
    {
        var manager = _sseManager;
        if (manager is null)
        {
            return;
        }

        _sseManager = null;
        manager.Stop();
        _sseConnected = false;
        _pollingManager?.Interval = _pollInterval;
    }

    // a callback from a stream that has since been replaced or stopped; caller holds _stateLock
    private bool IsSuperseded(SseManager manager) => !ReferenceEquals(manager, _sseManager);

    // SSE only prompts fetches; the cadence relaxes once the catch-up fetch succeeds (KB section 4.1)
    private void OnSseConnected(SseManager manager)
    {
        lock (_stateLock)
        {
            if (IsSuperseded(manager))
            {
                return;
            }

            _sseConnected = true;
        }

        _logger?.LogInformation("SSE connected");

        // catch up on anything announced while the stream was down
        _pollingManager?.RequestOutOfBandFetch();
    }

    private void OnSseDisconnected(SseManager manager)
    {
        lock (_stateLock)
        {
            if (IsSuperseded(manager) || !_sseConnected)
            {
                return;
            }

            _sseConnected = false;
            _logger?.LogWarning("SSE disconnected; poll interval tightened to {PollInterval}", _pollInterval);
            _pollingManager?.Interval = _pollInterval;
        }
    }

    private void OnSseMessage(SseManager manager, SseMessage message)
    {
        lock (_stateLock)
        {
            if (IsSuperseded(manager))
            {
                return;
            }
        }

        if (message.Type != SseMessage.TypeRefetchConfig)
        {
            _logger?.LogDebug("ignoring SSE message of type {Type}", message.Type);
            return;
        }

        // not dropped up front: second-precision timestamps can tie, and the conditional GET is cheap
        _pollingManager?.RequestOutOfBandFetch(message.LastModified);
    }

    // -- grace timer --

    // Caller holds _stateLock.
    private void StartGraceTimer()
    {
        CancelGraceTimer();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
        _graceCts = cts;
        _ = RunGraceTimerAsync(cts.Token);
    }

    // Caller holds _stateLock.
    private void CancelGraceTimer()
    {
        _graceCts?.Cancel();
        _graceCts = null;
    }

    private async Task RunGraceTimerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_gracePeriod, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // cancelled by a recovery or shutdown
        }

        lock (_stateLock)
        {
            // A recovery may have cancelled this timer after it fired but before it got the lock.
            if (!cancellationToken.IsCancellationRequested && _state == ProviderStatus.Stale)
            {
                _state = ProviderStatus.Error;
                _logger?.LogError("grace period expired, transitioning to Error");
                Emit(ProviderEventTypes.ProviderError, "grace period expired; CDN unreachable", errorType: ErrorType.General);
            }
        }
    }

    // -- state and events --

    private void SetState(ProviderStatus state)
    {
        lock (_stateLock)
        {
            _state = state;
        }
    }

    /// <summary>
    /// Announces a state change or configuration change to the SDK, which updates the provider's status and notifies
    /// the application's event handlers. Never blocks; the pump delivers it. Caller holds <c>_stateLock</c> when the
    /// event announces a state change.
    /// </summary>
    private void Emit(
        ProviderEventTypes type,
        string? message = null,
        IReadOnlyList<string>? flagsChanged = null,
        ErrorType? errorType = null)
    {
        _outbox.Writer.TryWrite(new ProviderEventPayload
        {
            ProviderName = ProviderName,
            Type = type,
            Message = message,
            FlagsChanged = flagsChanged is null ? null : new List<string>(flagsChanged),
            ErrorType = errorType,
        });
    }

    private void StartEventPump()
    {
        if (Interlocked.Exchange(ref _eventPumpStarted, 1) == 0)
        {
            _ = Task.Run(PumpEventsAsync);
        }
    }

    private async Task PumpEventsAsync()
    {
        var cancellationToken = _shutdownCts.Token;
        try
        {
            while (await _outbox.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_outbox.Reader.TryRead(out var payload))
                {
                    await EventChannel.Writer.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down: nobody is listening any more
        }
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
