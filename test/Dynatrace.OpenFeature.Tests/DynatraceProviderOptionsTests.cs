using System;
using System.Collections.Generic;

namespace Dynatrace.OpenFeature.Tests;

public class DynatraceProviderOptionsTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] vars)
    {
        var map = new Dictionary<string, string>();
        foreach (var (name, value) in vars)
        {
            map[name] = value;
        }

        return name => map.TryGetValue(name, out var value) ? value : null;
    }

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

    [Fact]
    public void WithEnvironment_NoEnv_KeepsDefaults()
    {
        var resolved = new DynatraceProviderOptions().WithEnvironment(Env());

        Assert.Null(resolved.ConfigOriginUri);
        Assert.Null(resolved.MetricsOriginUri);
        Assert.True(resolved.SseEnabled);
    }

    [Fact]
    public void WithEnvironment_UsesEnv_WhenNotSetInCode()
    {
        var resolved = new DynatraceProviderOptions().WithEnvironment(Env(
            ("DT_FM_CONFIG_ORIGIN", " https://cdn.env.example.com "),
            ("DT_FM_METRICS_ORIGIN", "https://metrics.env.example.com"),
            ("DT_FM_SSE_DISABLED", "true")));

        Assert.Equal("https://cdn.env.example.com/", resolved.ConfigOriginUri!.ToString());
        Assert.Equal("https://metrics.env.example.com/", resolved.MetricsOriginUri!.ToString());
        Assert.False(resolved.SseEnabled);
    }

    [Fact]
    public void WithEnvironment_CodeValuesWinOverEnv()
    {
        var options = new DynatraceProviderOptions
        {
            ConfigOrigin = "https://cdn.code.example.com",
            MetricsOrigin = "https://metrics.code.example.com",
            SseEnabled = true,
        };

        var resolved = options.WithEnvironment(Env(
            ("DT_FM_CONFIG_ORIGIN", "https://cdn.env.example.com"),
            ("DT_FM_METRICS_ORIGIN", "https://metrics.env.example.com"),
            ("DT_FM_SSE_DISABLED", "true")));

        Assert.Equal("https://cdn.code.example.com/", resolved.ConfigOriginUri!.ToString());
        Assert.Equal("https://metrics.code.example.com/", resolved.MetricsOriginUri!.ToString());
        Assert.True(resolved.SseEnabled); // explicit true in code beats the env
    }

    [Fact]
    public void WithEnvironment_CopiesRemainingOptionsAndLeavesOriginalUntouched()
    {
        var options = new DynatraceProviderOptions { MetricsFlushInterval = TimeSpan.FromSeconds(5) };

        var resolved = options.WithEnvironment(Env(("DT_FM_CONFIG_ORIGIN", "https://cdn.env.example.com")));

        Assert.Equal(TimeSpan.FromSeconds(5), resolved.MetricsFlushInterval);
        Assert.Null(options.ConfigOriginUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WithEnvironment_BlankEnv_IsTreatedAsUnset(string value)
    {
        var resolved = new DynatraceProviderOptions().WithEnvironment(Env(
            ("DT_FM_CONFIG_ORIGIN", value),
            ("DT_FM_METRICS_ORIGIN", value),
            ("DT_FM_SSE_DISABLED", value)));

        Assert.Null(resolved.ConfigOriginUri);
        Assert.Null(resolved.MetricsOriginUri);
        Assert.True(resolved.SseEnabled);
    }

    [Theory]
    [InlineData("true", false)]
    [InlineData(" TRUE ", false)]
    [InlineData("false", true)]
    [InlineData("False", true)]
    public void WithEnvironment_SseDisabled_IsParsedCaseInsensitively(string value, bool expectedSseEnabled)
    {
        var resolved = new DynatraceProviderOptions().WithEnvironment(Env(("DT_FM_SSE_DISABLED", value)));

        Assert.Equal(expectedSseEnabled, resolved.SseEnabled);
    }

    [Theory]
    [InlineData("DT_FM_CONFIG_ORIGIN", "not a uri")]
    [InlineData("DT_FM_CONFIG_ORIGIN", "https://example.com/some/path")]
    [InlineData("DT_FM_METRICS_ORIGIN", "ftp://example.com")]
    [InlineData("DT_FM_SSE_DISABLED", "yes")]
    [InlineData("DT_FM_SSE_DISABLED", "1")]
    public void WithEnvironment_InvalidEnv_ThrowsNamingTheEnvVar(string name, string value)
    {
        var ex = Assert.Throws<ArgumentException>(() => new DynatraceProviderOptions().WithEnvironment(Env((name, value))));

        Assert.Contains(name, ex.Message);
    }
}
