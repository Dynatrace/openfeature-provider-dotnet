using System;
using System.Threading.Tasks;
using LaunchDarkly.EventSource;
using LaunchDarkly.Logging;
using Microsoft.Extensions.Logging;

namespace Dynatrace.OpenFeature;

/// <summary>
/// The SSE change-notification stream. Reconnection, with backoff, is left to the event source (KB section 2.4).
/// </summary>
internal sealed class SseManager : IDisposable
{
    /// <summary>
    /// Drops and reconnects a stream that has gone silent, which raises no error on its own; tolerates one missed
    /// 60s keepalive (KB section 2.4).
    /// </summary>
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(150);

    private readonly Action<SseManager> _onConnected;
    private readonly Action<SseManager> _onDisconnected;
    private readonly Action<SseManager, SseMessage> _onMessage;
    private readonly ILogger? _logger;
    private readonly string _maskedUrl;

    private readonly object _lock = new object();
    private EventSource? _source;
    private volatile bool _stopped;

    /// <remarks>Each callback receives the raising manager, so a superseded stream can be told apart.</remarks>
    public SseManager(
        Uri url,
        Action<SseManager> onConnected,
        Action<SseManager> onDisconnected,
        Action<SseManager, SseMessage> onMessage,
        ILogger? logger = null)
    {
        Url = url;
        _onConnected = onConnected;
        _onDisconnected = onDisconnected;
        _onMessage = onMessage;
        _logger = logger;
        _maskedUrl = MaskUrl(url);
    }

    /// <summary>Compared by the provider to spot a rotation.</summary>
    public Uri Url { get; }

    /// <summary>Strips the query string, which carries the Ably key.</summary>
    internal static string MaskUrl(Uri url)
        => string.IsNullOrEmpty(url.Query) ? url.AbsoluteUri : url.GetLeftPart(UriPartial.Path) + "?<redacted>";

    public void Start()
    {
        EventSource source;
        lock (_lock)
        {
            if (_stopped || _source is not null)
            {
                return;
            }

            // the URL carries the key, so the event source must not log it
            var configuration = Configuration.Builder(Url)
                .ReadTimeout(IdleTimeout)
                .LogAdapter(Logs.None)
                .Build();
            source = new EventSource(configuration);
            source.Opened += OnOpened;
            source.Closed += OnClosed;
            source.Error += OnError;
            source.MessageReceived += OnMessageReceived;
            _source = source;
        }

        _ = RunAsync(source);
    }

    /// <summary>Closes the stream for good; handlers are detached first so a racing event goes nowhere.</summary>
    public void Dispose()
    {
        EventSource? source;
        lock (_lock)
        {
            _stopped = true;
            source = _source;
            _source = null;
        }

        if (source is null)
        {
            return;
        }

        source.Opened -= OnOpened;
        source.Closed -= OnClosed;
        source.Error -= OnError;
        source.MessageReceived -= OnMessageReceived;
        try
        {
            source.Dispose();
        }
        catch (Exception e)
        {
            _logger?.LogDebug("closing the SSE connection threw: {Message}", e.Message);
        }

        _logger?.LogInformation("SSE connection closed");
    }

    private async Task RunAsync(EventSource source)
    {
        try
        {
            // completes only once the source is closed; reconnects happen inside it
            await source.StartAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger?.LogWarning("SSE stream ended: {Message}", e.Message);
        }
    }

    private void OnOpened(object? sender, StateChangedEventArgs e)
    {
        if (_stopped)
        {
            return;
        }

        _logger?.LogInformation("SSE connection opened ({Url})", _maskedUrl);
        _onConnected(this);
    }

    // fires after an error too, then the source retries
    private void OnClosed(object? sender, StateChangedEventArgs e)
    {
        if (!_stopped)
        {
            _onDisconnected(this);
        }
    }

    private void OnError(object? sender, ExceptionEventArgs e)
    {
        if (_stopped)
        {
            return;
        }

        _logger?.LogWarning("SSE connection error ({Url}): {Message}", _maskedUrl, e.Exception?.Message);
        _onDisconnected(this);
    }

    private void OnMessageReceived(object? sender, MessageReceivedEventArgs e)
    {
        if (_stopped)
        {
            return;
        }

        var message = SseMessage.Parse(e.Message.Data);
        if (message is null)
        {
            _logger?.LogDebug("SSE message dropped: payload was not a recognisable envelope");
            return;
        }

        _logger?.LogDebug(
            "SSE message received (type={Type}, lastModified={LastModified})", message.Type, message.LastModified);
        _onMessage(this, message);
    }
}
