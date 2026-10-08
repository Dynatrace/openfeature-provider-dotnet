using System;

namespace Dynatrace.OpenFeature.Tests;

public class DynatraceProviderOptionsTests
{
    [Fact]
    public void ConfigOrigin_DefaultsToNull()
    {
        var options = new DynatraceProviderOptions();

        Assert.Null(options.ConfigOrigin);
        Assert.Null(options.ConfigOriginUri);
    }

    [Theory]
    [InlineData("https://cdn.eu.fm.dynatracelabs.com", "https://cdn.eu.fm.dynatracelabs.com/")]
    [InlineData("https://cdn.eu.fm.dynatracelabs.com/", "https://cdn.eu.fm.dynatracelabs.com/")]
    [InlineData("https://cdn.example.com:443", "https://cdn.example.com/")] // default port is normalised away
    [InlineData("  HTTP://LocalHost:8080  ", "http://localhost:8080/")]
    [InlineData("http://[::1]:8080", "http://[::1]:8080/")]
    [InlineData("http://my_service:8080", "http://my_service:8080/")]
    public void ConfigOrigin_ValidOrigin_IsStoredInCanonicalForm(string input, string expected)
    {
        var options = new DynatraceProviderOptions { ConfigOrigin = input };

        Assert.Equal(expected, options.ConfigOriginUri!.ToString());
    }

    [Fact]
    public void ConfigOrigin_SetToNull_ClearsOverride()
    {
        var options = new DynatraceProviderOptions { ConfigOrigin = "http://localhost:8080" };

        options.ConfigOrigin = null;

        Assert.Null(options.ConfigOriginUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a uri")]
    [InlineData("ftp://example.com")]
    [InlineData("https://example.com/some/path")]
    [InlineData("https://example.com//")]
    [InlineData("https://example.com/..")]
    [InlineData("https://example.com?x=1")]
    [InlineData("https://example.com#frag")]
    [InlineData("https://user:pass@example.com")]
    public void ConfigOrigin_InvalidOrigin_Throws(string input)
    {
        var options = new DynatraceProviderOptions();

        Assert.Throws<ArgumentException>(() => options.ConfigOrigin = input);
    }
}
