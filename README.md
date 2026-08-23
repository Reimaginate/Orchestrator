# Orchestrator

Orchestrator is Reimaginate's .NET workflow engine for defining and running
durable YAML-based workflows.

This repository contains the current public source snapshot for the core
Orchestrator runtime, command-line composition, source generation, and storage
components. Release tags identify the corresponding Orchestrator version.

The authoritative development repository is private. This generated snapshot
is provided for customer source access, transparency, and easier adoption.

## Documentation

For installation, configuration, concepts, tutorials, and operational guidance,
visit the [Orchestrator documentation](https://docs.reimaginate.online/orchestrator).

Package-level README files provide lightweight guidance beside the relevant
source projects.

## Repository layout

- `src` — core runtime, DSL, source-generation, command-line, Azure Storage,
  and Redis projects.
- `test` — shared test helpers and curated tests for the published components.
- `samples` — a safe ReferenceHost example using the published core runtime.
- `release` — the public package inventory used by the build.

## Build from source

Install the .NET SDK selected by `global.json`, then run:

```powershell
dotnet restore .\Reimaginate.Orchestrator.slnx
dotnet build .\Reimaginate.Orchestrator.slnx --configuration Release --no-restore
dotnet test --project .\test\Reimaginate.Orchestrator.Test.Unit\Reimaginate.Orchestrator.Test.Unit.csproj --configuration Release --no-restore
```

## Support and feedback

- Paid support, configuration assistance, and defect triage are available
  through [support@reimaginate.online](mailto:support@reimaginate.online).
- This repository does not provide support through GitHub Issues or
  Discussions. See [SUPPORT.md](SUPPORT.md).
- Follow [SECURITY.md](SECURITY.md) to report vulnerabilities privately.
- The repository does not accept external contributions. See
  [CONTRIBUTING.md](CONTRIBUTING.md).

## License

Orchestrator is licensed under the [MIT License](LICENSE). The Orchestrator YAML
DSL attribution notice is recorded in [NOTICE](NOTICE).
