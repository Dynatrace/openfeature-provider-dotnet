# Dynatrace OpenFeature Provider for .NET

The official [Dynatrace](https://dynatrace.com) [OpenFeature](https://openfeature.dev) provider for .NET server-side applications.

The provider evaluates flags in-process: on initialization it fetches your flag configuration from the Dynatrace CDN and keeps it up to date in the background, so evaluations are local and do not make a network call.

> **Status:** Early scaffolding — flag evaluation is not implemented yet and the provider is not ready for use.

## Installation

```bash
dotnet add package Dynatrace.OpenFeature --version 0.1.0-alpha # x-release-please-version
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

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

Apache 2.0 — see [LICENSE](LICENSE).
