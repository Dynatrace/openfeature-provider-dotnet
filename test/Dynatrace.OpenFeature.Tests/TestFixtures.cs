using System;
using System.IO;

namespace Dynatrace.OpenFeature.Tests;

internal static class TestFixtures
{
    public const string SdkKey = "dt01.server_us_abcdef1234.de848e97a9cc4cc78aae568e65f49a9d_a1b2c3d4e5";

    public static readonly string MinimalFlags = Load("minimal-flags.json");

    public static readonly string FlagConfig = Load("flag-config.json");

    private static string Load(string resourceName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", resourceName);
        if (!File.Exists(path))
        {
            throw new ArgumentException($"test resource not found: {resourceName}");
        }

        return File.ReadAllText(path);
    }
}
