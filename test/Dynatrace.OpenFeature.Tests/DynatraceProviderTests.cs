using System;
using System.Threading.Tasks;
using OpenFeature.Constant;

namespace Dynatrace.OpenFeature.Tests;

public class DynatraceProviderTests
{
    private const string SdkKey = "dt01.server_us_abcdef1234.de848e97a9cc4cc78aae568e65f49a9d_a1b2c3d4e5";

    [Fact]
    public void Metadata_NameIsDynatrace()
    {
        var provider = new DynatraceProvider(SdkKey);

        Assert.Equal("dynatrace", provider.GetMetadata().Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_BlankSdkKey_Throws(string? sdkKey)
    {
        Assert.Throws<ArgumentException>(() => new DynatraceProvider(sdkKey!));
    }

    [Fact]
    public async Task ResolveBoolean_ReturnsDefaultWithProviderNotReady_UntilImplemented()
    {
        var provider = new DynatraceProvider(SdkKey);

        var details = await provider.ResolveBooleanValueAsync("my-flag", true);

        Assert.True(details.Value);
        Assert.Equal(ErrorType.ProviderNotReady, details.ErrorType);
    }
}
