using System;

namespace Dynatrace.OpenFeature.Tests;

public class SseMessageTests
{
    [Theory]
    [InlineData("{\"type\":\"refetchConfig\",\"etag\":\"\\\"v2\\\"\",\"lastModified\":1704153600}")] // flat
    [InlineData("{\"id\":\"1\",\"data\":\"{\\\"type\\\":\\\"refetchConfig\\\",\\\"etag\\\":\\\"\\\\\\\"v2\\\\\\\"\\\",\\\"lastModified\\\":1704153600}\"}")] // Ably frame
    public void Parse_ReadsTheEnvelope(string data)
    {
        var message = SseMessage.Parse(data);

        Assert.NotNull(message);
        Assert.Equal(SseMessage.TypeRefetchConfig, message!.Type);
        Assert.Equal("\"v2\"", message.ETag);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1704153600), message.LastModified);
    }

    [Fact]
    public void Parse_OmitsAbsentOrMistypedFields()
    {
        var message = SseMessage.Parse("{\"type\":\"refetchConfig\",\"etag\":1,\"lastModified\":\"soon\"}");

        Assert.NotNull(message);
        Assert.Null(message!.ETag);
        Assert.Null(message.LastModified);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"type\":\"  \"}")]
    [InlineData("{\"type\":1}")]
    [InlineData("{\"data\":\"not json\"}")]
    public void Parse_ReturnsNullForAnUnusablePayload(string? data) => Assert.Null(SseMessage.Parse(data));
}
