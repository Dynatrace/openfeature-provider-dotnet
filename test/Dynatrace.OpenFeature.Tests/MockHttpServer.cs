using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
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
        var recorded = new RecordedRequest(request);
        MockResponse response;
        lock (_lock)
        {
            _requests.Add(recorded);
        }

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

    public RecordedRequest(HttpRequestMessage request)
    {
        Method = request.Method;
        Url = request.RequestUri!;
        _headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
    }

    public HttpMethod Method { get; }

    public Uri Url { get; }

    public string? GetHeader(string name) => _headers.TryGetValue(name, out var value) ? value : null;
}

internal sealed class MockResponse
{
    private readonly List<KeyValuePair<string, string>> _headers = new List<KeyValuePair<string, string>>();
    private HttpStatusCode _status = HttpStatusCode.OK;
    private string _body = string.Empty;
    private Exception? _failure;

    public static MockResponse Status(int status) => new MockResponse { _status = (HttpStatusCode)status };

    /// <summary>A response that makes the request fail at the network level.</summary>
    public static MockResponse NetworkFailure(string message) => new MockResponse { _failure = new HttpRequestException(message) };

    public MockResponse Body(string body)
    {
        _body = body;
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

        var message = new HttpResponseMessage(_status) { Content = new StringContent(_body) };
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
