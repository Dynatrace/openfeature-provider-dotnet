using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dynatrace.OpenFeature;

/// <summary>
/// An SSE change notification (KB section 1.5). Notification-only: it never carries flag configuration, it only
/// prompts a re-fetch.
/// </summary>
internal sealed class SseMessage
{
    /// <summary>Server-side signal to re-fetch the flag configuration from the CDN.</summary>
    internal const string TypeRefetchConfig = "refetchConfig";

    private const long MsPerSecond = 1_000;

    private SseMessage(string type, string? eTag, DateTimeOffset? lastModified)
    {
        Type = type;
        ETag = eTag;
        LastModified = lastModified;
    }

    /// <summary>Discriminator; unknown types are ignored.</summary>
    public string Type { get; }

    /// <summary>Quoted ETag of the new resource, if announced.</summary>
    public string? ETag { get; }

    /// <summary>When the new resource was modified, if announced (epoch seconds on the wire).</summary>
    public DateTimeOffset? LastModified { get; }

    /// <summary>
    /// Parses the Ably frame (the envelope as a JSON string under <c>data</c>) or the flat envelope. Returns
    /// <c>null</c> for invalid JSON or a blank <c>type</c>.
    /// </summary>
    public static SseMessage? Parse(string? data)
    {
        var outer = ParseObject(data);
        if (outer is null)
        {
            return null;
        }

        var inner = StringValue(outer["data"]) is { } frameData ? ParseObject(frameData) : outer;
        if (inner is null)
        {
            return null;
        }

        var type = StringValue(inner["type"]);
        if (type is null || string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        return new SseMessage(type, StringValue(inner["etag"]), EpochSecondsValue(inner["lastModified"]));
    }

    private static JsonObject? ParseObject(string? json)
    {
        if (json is null || string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? StringValue(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static DateTimeOffset? EpochSecondsValue(JsonNode? node)
    {
        if (!(node is JsonValue value) || !value.TryGetValue<double>(out var seconds)
            || double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * MsPerSecond));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
