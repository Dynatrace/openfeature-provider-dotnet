using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Dynatrace.OpenFeature;

/// <summary>
/// Typed view of the CDN config JSON body.
/// </summary>
/// <remarks>
/// <para>
/// Only the fields the provider needs are mapped; the bulk of the body is the flagd flag definitions, which are
/// handed to the flagd core as the raw string rather than parsed here.
/// </para>
/// <para>
/// Unknown properties are ignored (the <c>System.Text.Json</c> default), so the server can add new fields, such as new
/// <c>eventStreams</c> entry attributes, without breaking older SDK versions.
/// </para>
/// </remarks>
internal sealed class CdnConfigJson
{
    /// <summary><c>eventStreams[].type</c> of the SSE stream.</summary>
    internal const string StreamTypeSse = "sse";

    private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

        // The body is mostly flag definitions, whose targeting rules nest. This is the nesting limit of the Java
        // provider's JSON parser; the System.Text.Json default of 64 would reject deep but legitimate configs.
        MaxDepth = 1000,
    };

    public List<EventStream?>? EventStreams { get; set; }

    /// <summary>
    /// Parses the CDN config body. Returns <c>null</c> if the payload is blank or not usable JSON; callers should treat
    /// that as "no usable config".
    /// </summary>
    public static CdnConfigJson? Parse(string? json)
    {
        if (json is null || string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CdnConfigJson>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves the SSE stream URL the config advertises (KB section 1.5): the first <c>sse</c> entry's flat
    /// <c>url</c>, else its <c>endpoint</c>, whose origin defaults to <paramref name="fallbackOrigin"/>. Returns
    /// <c>null</c> when the config advertises no usable stream, which means polling only.
    /// </summary>
    public static Uri? ExtractSseUrl(string? json, Uri fallbackOrigin)
    {
        // the first sse entry wins; a later one is not a fallback for an unusable first
        var stream = Parse(json)?.EventStreams?.FirstOrDefault(s => s?.Type == StreamTypeSse);
        if (stream is null)
        {
            return null;
        }

        if (!IsBlank(stream.Url))
        {
            return ToHttpUri(stream.Url!);
        }

        var requestUri = stream.Endpoint?.RequestUri;
        if (IsBlank(requestUri))
        {
            return null;
        }

        var origin = stream.Endpoint!.Origin;
        return ToHttpUri((IsBlank(origin) ? fallbackOrigin.GetLeftPart(UriPartial.Authority) : origin!.TrimEnd('/')) + requestUri);
    }

    private static bool IsBlank(string? value) => value is null || string.IsNullOrWhiteSpace(value);

    private static Uri? ToHttpUri(string value)
        => Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;

    /// <summary>
    /// Structured endpoint alternative to the flat <c>url</c> field.
    /// </summary>
    internal sealed class Endpoint
    {
        public string? Origin { get; set; }

        public string? RequestUri { get; set; }
    }

    /// <summary>A single entry in the <c>eventStreams</c> array. Unknown attributes are ignored.</summary>
    internal sealed class EventStream
    {
        public string? Type { get; set; }

        public string? Url { get; set; }

        public Endpoint? Endpoint { get; set; }
    }
}
