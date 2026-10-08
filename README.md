# Dynatrace OpenFeature Provider for .NET

The official [Dynatrace](https://dynatrace.com) [OpenFeature](https://openfeature.dev) provider for .NET server-side applications.

The provider evaluates flags in-process: on initialization it fetches your flag configuration from the Dynatrace CDN and keeps it up to date in the background, so evaluations are local and do not make a network call.

> **Status:** Early alpha — not yet ready for production use. The flag configuration is refreshed by polling
> every 10 seconds; push-based change notifications (SSE) and evaluation metrics are not implemented yet.

## Installation

```bash
dotnet add package Dynatrace.OpenFeature --version 0.0.1-alpha # x-release-please-version
```

## Usage

```csharp
using Dynatrace.OpenFeature;
using OpenFeature;
using OpenFeature.Model;

await Api.Instance.SetProviderAsync(new DynatraceProvider(Environment.GetEnvironmentVariable("DT_SDK_KEY")!));

var client = Api.Instance.GetClient();
var context = EvaluationContext.Builder().SetTargetingKey("user-123").Build();
bool enabled = await client.GetBooleanValueAsync("my-flag", false, context);

// On shutdown:
await Api.Instance.ShutdownAsync();
```

Once the provider is registered, all flag evaluation goes through the OpenFeature API. See the
[OpenFeature .NET SDK documentation](https://openfeature.dev/docs/reference/sdks/server/dotnet/)
for evaluation contexts, hooks, events, and the rest of the SDK surface.

## Configuration

Pass a `DynatraceProviderOptions` as the second constructor argument:

```csharp
var options = new DynatraceProviderOptions
{
    ConfigOrigin = "https://cdn.eu.fm.dynatracelabs.com",
    Logger = loggerFactory.CreateLogger("Dynatrace.OpenFeature"),
};

await Api.Instance.SetProviderAsync(new DynatraceProvider(sdkKey, options));
```

| Option | Type | Default | Description |
| --- | --- | --- | --- |
| `ConfigOrigin` | `string?` | derived from SDK key | Override the flag configuration origin (scheme, host and optional port; no path). |
| `Logger` | `ILogger?` | none | Receives lifecycle messages and flag configuration schema warnings. |

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

Apache 2.0 — see [LICENSE](LICENSE).
