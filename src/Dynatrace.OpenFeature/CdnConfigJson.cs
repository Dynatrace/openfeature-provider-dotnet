using System.Collections.Generic;
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
