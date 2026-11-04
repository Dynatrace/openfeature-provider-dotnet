# Dynatrace OpenFeature Provider for .NET

The official [Dynatrace](https://dynatrace.com) [OpenFeature](https://openfeature.dev) provider for .NET server-side applications.

The provider evaluates flags in-process: on initialization it fetches your flag configuration from the Dynatrace CDN and keeps it up to date in the background, so evaluations are local and do not make a network call.

> **Status:** Early alpha; not yet ready for production use.

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

## Configuration

Pass a `DynatraceProviderOptions` as the second constructor argument:

```csharp
var options = new DynatraceProviderOptions
{
    ConfigOrigin = "https://cdn.eu.fm.dynatracelabs.com",
    MetricsOrigin = "https://metrics.eu.fm.dynatracelabs.com",
    Logger = loggerFactory.CreateLogger("Dynatrace.OpenFeature"),
};

await Api.Instance.SetProviderAsync(new DynatraceProvider(sdkKey, options));
```

| Option | Type | Default | Description |
| --- | --- | --- | --- |
| `ConfigOrigin` | `string?` | derived from SDK key | Override the flag configuration origin (scheme, host and optional port; no path). |
| `MetricsOrigin` | `string?` | derived from SDK key | Override the evaluation metrics ingest origin (scheme, host and optional port; no path). `/v1/metrics` is appended. |
| `SseEnabled` | `bool` | `true` | Subscribe to streamed change notifications; when `false`, rely on polling alone. |
| `MetricsFlushInterval` | `TimeSpan` | 30 seconds | How often aggregated evaluation metrics are sent. Zero or negative values fall back to the default with a warning. |
| `Logger` | `ILogger?` | none | Receives lifecycle messages and flag configuration schema warnings. |

## Keeping the configuration current

The provider keeps its configuration current two ways at once:

- **Change notifications.** It opens a Server-Sent Events connection to the stream your configuration advertises, and
  re-fetches as soon as a change is announced. The connection reconnects on its own, and the provider follows the
  stream URL if it is rotated.
- **Polling.** A conditional `GET` against the CDN runs every 10 seconds. While the notification stream is connected
  and fetches succeed this relaxes to 10 minutes, since it is only a safety net; it tightens back to 10 seconds
  whenever the stream is down or a fetch fails.

Neither interval is configurable. Set `SseEnabled = false` to skip the stream entirely; polling then stays at its
10 second cadence, so changes still arrive, just less promptly.

## Evaluation metrics

The provider counts every flag evaluation made through the OpenFeature client, by flag key, variant, reason and error
code in 10-second buckets, and sends the aggregate to the Dynatrace metrics endpoint in the background. It is flushed
periodically, when many distinct flags have accumulated, when a new flag configuration is loaded, and on shutdown, so
call `Api.Instance.ShutdownAsync()` before your application exits to deliver the final batch. Evaluations are never
slowed by this: recording is in-memory, and delivery happens off the evaluation path.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

Apache 2.0 — see [LICENSE](LICENSE).
