using System.Linq;

namespace Dynatrace.OpenFeature.Tests;

public class CdnConfigJsonTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_BlankInput_ReturnsNull(string? json)
    {
        Assert.Null(CdnConfigJson.Parse(json));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"eventStreams\":\"not-an-array\"}")]
    [InlineData("{\"eventStreams\":[{\"type\":{\"nested\":1}}]}")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"flags\": not-valid-json}")]
    public void Parse_UnusableShape_ReturnsNull(string json)
    {
        Assert.Null(CdnConfigJson.Parse(json));
    }

    [Fact]
    public void Parse_AcceptsAnEmptyObject()
    {
        var parsed = CdnConfigJson.Parse("{}");

        Assert.NotNull(parsed);
        Assert.Null(parsed!.EventStreams);
    }

    [Fact]
    public void Parse_MapsEventStreams()
    {
        const string config = "{\"flags\":{},\"eventStreams\":["
                              + "{\"type\":\"sse\",\"url\":\"https://realtime.ably.io/event-stream?key=abc&channels=xyz\",\"inactivityDelaySec\":120},"
                              + "{\"type\":\"sse\",\"endpoint\":{\"origin\":\"https://sse.example.com\",\"requestUri\":\"/stream?ch=abc\"}}"
                              + "]}";

        var parsed = CdnConfigJson.Parse(config);

        Assert.NotNull(parsed);
        var streams = parsed!.EventStreams!;
        Assert.Equal(2, streams.Count);
        Assert.Equal("sse", streams[0]!.Type);
        Assert.Equal("https://realtime.ably.io/event-stream?key=abc&channels=xyz", streams[0]!.Url);
        Assert.Null(streams[0]!.Endpoint);
        Assert.Null(streams[1]!.Url);
        Assert.Equal("https://sse.example.com", streams[1]!.Endpoint!.Origin);
        Assert.Equal("/stream?ch=abc", streams[1]!.Endpoint!.RequestUri);
    }

    [Fact]
    public void Parse_MissingFieldsStayNull()
    {
        var parsed = CdnConfigJson.Parse("{\"eventStreams\":[{\"type\":\"sse\",\"endpoint\":{\"requestUri\":\"/s\"}}]}");

        var stream = parsed!.EventStreams!.Single()!;
        Assert.Null(stream.Url);
        Assert.Null(stream.Endpoint!.Origin);
        Assert.Equal("/s", stream.Endpoint.RequestUri);
    }

    [Fact]
    public void Parse_ToleratesNullEntries()
    {
        var parsed = CdnConfigJson.Parse("{\"eventStreams\":[null,{\"type\":\"sse\"}]}");

        Assert.Equal(2, parsed!.EventStreams!.Count);
        Assert.Null(parsed.EventStreams[0]);
        Assert.Equal("sse", parsed.EventStreams[1]!.Type);
    }

    [Fact]
    public void Parse_IgnoresUnknownPropertiesAtEveryLevel()
    {
        // the server can add fields without breaking older SDK versions
        const string config = "{\"someNewTopLevelField\":{\"a\":1},\"flags\":{},"
                              + "\"eventStreams\":[{\"type\":\"sse\",\"url\":\"https://x.example.com\",\"futureAttribute\":[1,2],"
                              + "\"endpoint\":{\"requestUri\":\"/s\",\"alsoNew\":true}}]}";

        var parsed = CdnConfigJson.Parse(config);

        Assert.NotNull(parsed);
        var stream = parsed!.EventStreams!.Single()!;
        Assert.Equal("https://x.example.com", stream.Url);
        Assert.Equal("/s", stream.Endpoint!.RequestUri);
    }

    [Fact]
    public void Parse_IsCaseSensitiveLikeJava()
    {
        // "EventStreams" is not "eventStreams"
        var parsed = CdnConfigJson.Parse("{\"EventStreams\":[{\"type\":\"sse\",\"url\":\"https://x.example.com\"}]}");

        Assert.NotNull(parsed);
        Assert.Null(parsed!.EventStreams);
    }

    [Fact]
    public void Parse_AcceptsTheRealFlagConfigFixture()
    {
        var parsed = CdnConfigJson.Parse(TestFixtures.FlagConfig);

        Assert.NotNull(parsed); // the flag definitions are skipped, not rejected
        Assert.Null(parsed!.EventStreams);
    }

    [Fact]
    public void Parse_AcceptsDeeplyNestedFlagDefinitions()
    {
        // targeting rules nest; the flag definitions must not trip the parser's depth limit
        var nested = string.Concat(Enumerable.Repeat("{\"a\":", 200)) + "1" + new string('}', 200);
        var config = "{\"flags\":" + nested + ",\"eventStreams\":[{\"type\":\"sse\",\"url\":\"https://x.example.com\"}]}";

        var parsed = CdnConfigJson.Parse(config);

        Assert.Equal("https://x.example.com", parsed!.EventStreams!.Single()!.Url);
    }
}
