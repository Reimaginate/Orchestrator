# Reimaginate.Orchestrator.CommandLine

Reusable command-line commands and host wiring for Orchestrator workflow applications.

Typical usage:

1. Reference `Reimaginate.Orchestrator.CommandLine`.
2. Reference `Reimaginate.Orchestrator.Common`.
3. Define a partial workflow action resolver in the host project.
4. Register `AddOrchestratorCommandLine<...>(configuration)` and the generated resolver type.

Workflow execution persistence can be overridden for a single command invocation:

```powershell
dotnet run -- start workflow DataMaintenance\idempotent.workflow.yaml --checkpoints Disabled --workflow-instances Disabled
dotnet run -- resume workflow DataMaintenance\idempotent.workflow.yaml <workflow-instance-id> --checkpoints Disabled
dotnet run -- process events --checkpoints Failure --workflow-instances Failure
```

`--checkpoints` and `--workflow-instances` accept `Enabled`, `Disabled`, or `Failure`. Command-line values override the configured workflow execution policy only for the workflow runs started or resumed by that command.

Event processing diagnostics can be enabled for a single command invocation:

```powershell
dotnet run -- process events --log-events
dotnet run -- process events --dry-run --log-events
```

`--log-events` writes per-message matching diagnostics. `--dry-run` evaluates the received batch, logs predicted actions, and releases messages without starting or resuming workflows and without completing or dead-lettering messages.
