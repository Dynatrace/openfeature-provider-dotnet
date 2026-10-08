using System;

namespace Dynatrace.OpenFeature;

/// <summary>
/// Result of a CDN fetch.
/// </summary>
/// <remarks>
/// <see cref="Body"/> is <c>null</c> on HTTP 304 (Not Modified); callers should skip updating the flag
/// configuration in that case. <see cref="Skipped"/> is a distinct outcome from any HTTP status: no request
/// was made.
/// </remarks>
internal sealed class FetchResult
{
    /// <summary>Sentinel status for <see cref="Skipped"/>; never a real HTTP status.</summary>
    private const int StatusSkipped = 0;

    public FetchResult(int statusCode, string? body, string? eTag, DateTimeOffset? lastModified)
    {
        StatusCode = statusCode;
        Body = body;
        ETag = eTag;
        LastModified = lastModified;
    }

    /// <summary>
    /// No request was made: a fetch was already in flight, or a Retry-After window is open.
    /// </summary>
    public static FetchResult Skipped { get; } = new FetchResult(StatusSkipped, null, null, null);

    public int StatusCode { get; }

    /// <summary>The response body, or <c>null</c> for 304 Not Modified.</summary>
    public string? Body { get; }

    /// <summary>The ETag value from the response, or <c>null</c> if absent.</summary>
    public string? ETag { get; }

    /// <summary>The Last-Modified value from the response, or <c>null</c> if absent.</summary>
    public DateTimeOffset? LastModified { get; }

    public bool IsSkipped => StatusCode == StatusSkipped;

    public bool IsNotModified => StatusCode == 304;

    public bool IsRateLimited => StatusCode == 429;

    public bool IsSuccess => StatusCode >= 200 && StatusCode < 300;
}
