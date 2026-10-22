# Contributing

## Prerequisites

- .NET SDK 10.0 (the solution uses the `.slnx` format, which needs SDK 9.0.200 or later)
- .NET 8.0 SDK/runtime as well, since the tests multi-target `net8.0` and `net10.0`
- The `fm-provider-test-harness` git submodule, which the e2e project needs to build:
  clone with `--recurse-submodules`, or run `git submodule update --init` afterwards
  
The library itself targets `netstandard2.0`.

## Building

```bash
dotnet build
```
`dotnet build` builds the whole solution, including the e2e project, so initialize the submodule first (see Prerequisites).
## Testing

```bash
dotnet test test/Dynatrace.OpenFeature.Tests
```

Unit tests must not require network access.

The e2e suite runs the shared [fm-provider-test-harness](https://github.com/Dynatrace/fm-provider-test-harness) gherkin
spec against the provider. It needs the submodule and a running Docker daemon (the mock server runs in a container):

```bash
git submodule update --init
dotnet test test/Dynatrace.OpenFeature.E2e
```

Set `MOCKSERVER_IMAGE` to run against a locally built mock server image.

## Code style

- Follow existing conventions in the codebase; `.editorconfig` is enforced in CI:

  ```bash
  dotnet format --verify-no-changes
  ```

- Nullable reference types are enabled and warnings are treated as errors.
- Minimize non-test dependencies.
- Keep public API surface minimal. New public members need a clear reason to exist.
- Log at `Debug` for routine operations, `Warning` for recoverable errors, `Information` for lifecycle events.
- All provider behavior must align with [providers.md](https://github.com/Dynatrace/feature-management-knowledge-base) (sections 1.1, 2.1, 2.3, 2.4, 3.1, 4.1, 6).

## Commits and pull requests

PR titles (and single-commit messages) must follow [Conventional Commits](https://www.conventionalcommits.org),
e.g. `feat: add SSE change notifications`. CI enforces this, and release notes are generated from them.

## Release

Releases are managed via [release-please](https://github.com/googleapis/release-please). Merge the generated
release PR to publish the package to NuGet.
