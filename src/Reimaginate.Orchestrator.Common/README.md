# Reimaginate.Orchestrator.Common

Orchestrator is Reimaginate's opinionated YAML workflow and durability layer built on the [Microsoft Agent Framework Workflows runtime](https://github.com/microsoft/agent-framework).

Core Orchestrator runtime services for custom workflow hosts.

This package is the primary consumer entry point for custom hosts. It provides the runtime services and carries the workflow action source generator transitively, so a host only needs to:

1. Reference `Reimaginate.Orchestrator.Common`.
2. Declare a partial `WorkflowActionResolver`.
3. Call `AddOrchestratorServices(...)` with that resolver type.

See `Config/README.md` for composition examples.
