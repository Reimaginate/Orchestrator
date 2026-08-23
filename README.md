# Orchestrator

Orchestrator is Reimaginate's opinionated YAML workflow and durability layer
built on the Microsoft Agent Framework Workflows runtime.

This repository contains the current public source snapshot for the core
Orchestrator runtime, command-line composition, source generation, and storage
components. Release tags identify the corresponding Orchestrator version.

The authoritative development repository is private. This generated snapshot
is provided for customer source access, transparency, and easier adoption.

## Relationship to Microsoft Agent Framework

[Microsoft Agent Framework](https://github.com/microsoft/agent-framework)
provides the underlying workflow graph execution, executors, event streaming,
and checkpoint primitives. See Microsoft's
[Workflow Builder & Execution documentation](https://learn.microsoft.com/en-us/agent-framework/concepts/workflows/builder-and-execution)
for the foundation Orchestrator builds on.

Orchestrator adds its YAML DSL and compiler, durability and persistence
policies, workflow-instance storage, event correlation, storage adapters,
hosting composition, command-line experience, source generation, and authoring
tooling. Orchestrator currently uses deterministic workflow primitives and does
not require an AI agent, LLM, or model provider.

Orchestrator is an independent Reimaginate project and is not affiliated with,
endorsed by, or sponsored by Microsoft.

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
