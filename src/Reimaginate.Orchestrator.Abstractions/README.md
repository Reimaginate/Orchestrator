# Reimaginate.Orchestrator.Abstractions

Orchestrator is Reimaginate's opinionated YAML workflow and durability layer built on the [Microsoft Agent Framework Workflows runtime](https://github.com/microsoft/agent-framework).

Contracts and attributes for Orchestrator workflow hosts and action libraries.

This package includes:

- `WorkflowActionAttribute` for callable workflow requests
- `WorkflowActionResolverAttribute` and `ScanAssemblyAttribute` for resolver declarations
- `IWorkflowActionResolver` and related shared contracts

Use this package when you need the lightweight consumer-facing abstractions without the full runtime.
