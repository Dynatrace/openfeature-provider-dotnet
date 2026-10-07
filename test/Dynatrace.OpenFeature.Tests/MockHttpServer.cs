using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Dynatrace.OpenFeature.Tests;

/// <summary>
/// An in-process stand-in for the CDN, modelled on OkHttp's <c>MockWebServer</c> as used by the Java provider's
/// tests: responses are enqueued (or produced by a <see cref="Dispatcher"/>), and every request is recorded.
/// </summary>
internal sealed class MockHttpServer : HttpMessageHandler
{
    private readonly Queue<MockResponse> _responses = new Queue<MockResponse>();
    private readonly List<RecordedRequest> _requests = new List<RecordedRequest>();
    private readonly Channel<RecordedRequest> _unclaimed = Channel.CreateUnbounded<RecordedRequest>();
    private readonly object _lock = new object();

    /// <summary>When set, answers every request instead of the queue.</summary>
    public Func<RecordedRequest, CancellationToken, Task<MockResponse>>? Dispatcher { get; set; }

    public int RequestCount
    {
        get
        {
            lock (_lock)
            {
                return _requests.Count;
            }
        }
    }

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_lock)
            {
                return _requests.ToList();
            }
        }
    }

    /// <summary>
    /// Waits for the next recorded request that has not been taken yet, like <c>MockWebServer.takeRequest</c>.
    /// Returns <c>null</c> if none arrives within the timeout.
    /// </summary>
    public async Task<RecordedRequest?> TakeRequestAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await _unclaimed.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public void Enqueue(MockResponse response)
    {
        lock (_lock)
        {
            _responses.Enqueue(response);
        }
    }

    public HttpClient CreateClient() => new HttpClient(this) { Timeout = Timeout.InfiniteTimeSpan };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var recorded = await RecordedRequest.CreateAsync(request).ConfigureAwait(false);
        MockResponse response;
        lock (_lock)
        {
            _requests.Add(recorded);
        }

        _unclaimed.Writer.TryWrite(recorded);

        if (Dispatcher is not null)
        {
            response = await Dispatcher(recorded, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            lock (_lock)
            {
                if (_responses.Count == 0)
                {
                    throw new HttpRequestException("MockHttpServer: no response enqueued");
                }

                response = _responses.Dequeue();
            }
        }

        return response.ToHttpResponseMessage();
    }
}

internal sealed class RecordedRequest
{
    private readonly Dictionary<string, string> _headers;

    private RecordedRequest(HttpMethod method, Uri url, Dictionary<string, string> headers, string body)
    {
        Method = method;
        Url = url;
        _headers = headers;
        Body = body;
    }

    public HttpMethod Method { get; }

    public Uri Url { get; }

    public string Path => Url.AbsolutePath;

    public string Body { get; }

    public static async Task<RecordedRequest> CreateAsync(HttpRequestMessage request)
    {
        // Captured now: the content is disposed once the request completes.
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = string.Empty;
        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }

            body = await request.Content.ReadAsStringAsync().ConfigureAwait(false);
        }

        return new RecordedRequest(request.Method, request.RequestUri!, headers, body);
    }

    public string? GetHeader(string name) => _headers.TryGetValue(name, out var value) ? value : null;
}

internal sealed class MockResponse
{
    private readonly List<KeyValuePair<string, string>> _headers = new List<KeyValuePair<string, string>>();
    private HttpStatusCode _status = HttpStatusCode.OK;
    private string _body = string.Empty;
    private byte[]? _bodyBytes;
    private Exception? _failure;

    public static MockResponse Status(int status) => new MockResponse { _status = (HttpStatusCode)status };

    /// <summary>A response that makes the request fail at the network level.</summary>
    public static MockResponse NetworkFailure(string message) => new MockResponse { _failure = new HttpRequestException(message) };

    public MockResponse Body(string body)
    {
        _body = body;
        return this;
    }

    /// <summary>A raw body, sent without a Content-Type unless one is added with <see cref="Header"/>.</summary>
    public MockResponse BodyBytes(byte[] body)
    {
        _bodyBytes = body;
        return this;
    }

    public MockResponse Header(string name, string value)
    {
        _headers.Add(new KeyValuePair<string, string>(name, value));
        return this;
    }

    public HttpResponseMessage ToHttpResponseMessage()
    {
        if (_failure is not null)
        {
            throw _failure;
        }

        var message = new HttpResponseMessage(_status)
        {
            Content = _bodyBytes is not null ? new ByteArrayContent(_bodyBytes) : new StringContent(_body),
        };
        foreach (var header in _headers)
        {
            // entity headers (e.g. Last-Modified) live on the content
            if (!message.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                message.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return message;
    }
}
