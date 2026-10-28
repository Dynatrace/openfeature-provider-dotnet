using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Dynatrace.OpenFeature.E2e;

/// <summary>Client for the containerized harness mock server and its control plane.</summary>
internal sealed class MockServer
{
    private const int ContainerPort = 8080;

    /// <summary>The only path the mock server serves an SSE stream on.</summary>
    internal const string SsePath = "/sse";
    private const string ImageRepo = "ghcr.io/dynatrace/fm-provider-mock-server";

    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    private static IContainer? _container;

    private MockServer(string origin)
    {
        Origin = origin;
    }

    /// <summary>Scheme, host and port the provider is pointed at.</summary>
    public string Origin { get; }

    public static MockServer? Current { get; private set; }

    /// <summary>Starts the shared container; the image tag comes from the pinned harness, unless MOCKSERVER_IMAGE is set.</summary>
    public static async Task StartAsync()
    {
        var image = Environment.GetEnvironmentVariable("MOCKSERVER_IMAGE")
                    ?? $"{ImageRepo}:v{File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "version.txt")).Trim()}";
        _container = new ContainerBuilder(image)
            .WithPortBinding(ContainerPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(ContainerPort).ForPath("/__control__/health")))
            .Build();
        await _container.StartAsync().ConfigureAwait(false);
        Current = new MockServer($"http://{_container.Hostname}:{_container.GetMappedPublicPort(ContainerPort)}");
    }

    public static async Task StopAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Clears CDN responses, recorded requests and rate-limit state.</summary>
    public Task ResetAsync() => SendAsync(HttpMethod.Post, "/__control__/reset", null);

    /// <summary>Sets the ordered CDN response program; the last entry repeats once exhausted.</summary>
    public Task SetCdnResponsesAsync(IEnumerable<CdnResponse> responses)
    {
        var program = new JsonArray();
        foreach (var r in responses)
        {
            var entry = new JsonObject { ["status"] = r.Status };
            if (r.Body is not null)
            {
                entry["body"] = r.Body;
            }

            if (r.DelayMs > 0)
            {
                entry["delayMs"] = r.DelayMs;
            }

            var headers = new JsonObject();
            foreach (var header in r.Headers)
            {
                headers[header.Key] = header.Value;
            }

            entry["headers"] = headers;
            program.Add(entry);
        }

        var body = new JsonObject { ["responses"] = program, ["repeatLast"] = true };
        return SendAsync(HttpMethod.Put, "/__control__/cdn/responses", body.ToJsonString());
    }

    /// <summary>CDN requests since the last reset, in arrival order.</summary>
    public async Task<IReadOnlyList<RecordedRequest>> FetchCdnRequestsAsync()
    {
        var json = await SendAsync(HttpMethod.Get, "/__control__/cdn/requests", null).ConfigureAwait(false);
        var requests = JsonNode.Parse(json)?["requests"]?.AsArray() ?? new JsonArray();
        return requests
            .Select(r => new RecordedRequest(
                r!["headers"]?.AsObject().ToDictionary(
                    h => h.Key.ToLowerInvariant(), h => h.Value?.GetValue<string>() ?? string.Empty)
                ?? new Dictionary<string, string>(),
                r["receivedAtMs"]?.GetValue<long>() ?? 0))
            .ToList();
    }

    public async Task<int> CountCdnRequestsAsync() => (await FetchCdnRequestsAsync().ConfigureAwait(false)).Count;

    /// <summary>SSE subscribers currently connected.</summary>
    public async Task<int> CountSseClientsAsync()
    {
        var json = await SendAsync(HttpMethod.Get, "/__control__/sse/clients", null).ConfigureAwait(false);
        return JsonNode.Parse(json)?["clients"]?.GetValue<int>() ?? 0;
    }

    /// <summary>Drops every SSE subscriber and refuses new connections for the outage, or until reset when omitted.</summary>
    public Task DropSseAsync(TimeSpan? outage = null) => SendAsync(
        HttpMethod.Post,
        "/__control__/sse/disconnect",
        outage is null ? null : new JsonObject { ["reconnectSeconds"] = outage.Value.TotalSeconds }.ToJsonString());

    /// <summary>Broadcasts the payload as the data of one SSE event to every connected subscriber.</summary>
    public Task EmitSseAsync(string payload) => SendAsync(HttpMethod.Post, "/__control__/sse/emit", payload);

    private static async Task<string> SendAsync(HttpMethod method, string path, string? json)
    {
        using var request = new HttpRequestMessage(method, Current!.Origin + path);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if ((int)response.StatusCode >= 300)
        {
            throw new InvalidOperationException($"control call {method} {path} failed: HTTP {(int)response.StatusCode} {body}");
        }

        return body;
    }
}

/// <summary>One CDN response the mock serves.</summary>
internal sealed class CdnResponse
{
    public CdnResponse(int status, string? body = null)
    {
        Status = status;
        Body = body;
    }

    public int Status { get; }

    // null is no body, as opposed to an empty one
    public string? Body { get; }

    public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>();

    public int DelayMs { get; set; }

    public CdnResponse WithHeader(string name, string value)
    {
        Headers[name] = value;
        return this;
    }
}

/// <summary>A CDN request the mock recorded; header names are lowercased.</summary>
internal sealed class RecordedRequest
{
    private readonly Dictionary<string, string> _headers;

    public RecordedRequest(Dictionary<string, string> headers, long receivedAtMs)
    {
        _headers = headers;
        ReceivedAtMs = receivedAtMs;
    }

    // epoch millis at arrival, before any response delay
    public long ReceivedAtMs { get; }

    public bool HasHeader(string name) => _headers.ContainsKey(name.ToLowerInvariant());

    public string? GetHeader(string name) => _headers.TryGetValue(name.ToLowerInvariant(), out var value) ? value : null;
}
