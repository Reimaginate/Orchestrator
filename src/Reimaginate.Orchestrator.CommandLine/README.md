# Reimaginate.Orchestrator.CommandLine

Orchestrator is Reimaginate's opinionated YAML workflow and durability layer built on the [Microsoft Agent Framework Workflows runtime](https://github.com/microsoft/agent-framework).

Reusable command-line commands and host wiring for Orchestrator workflow applications.

Typical usage:

1. Reference `Reimaginate.Orchestrator.CommandLine`.
2. Reference `Reimaginate.Orchestrator.Common`.
3. Define a partial workflow action resolver in the host project.
4. Register runtime services and the generated resolver type, then call `services.AddOrchestratorCommandLine(configuration)`. Hosts using `AddOrchestratorHost(configuration, ...)` receive this registration automatically.

## Final workflow output

`start workflow` and `resume workflow` write an indented JSON envelope containing `workflowType`, `workflowInstanceId`, and `finalOutput` after successful execution when final output is present.

`Orchestrator:CommandLine:EmitFinalOutput` controls this envelope and defaults to `true` for backward compatibility. Set it to `false` to suppress the envelope before serialization:

```json
{
  "Orchestrator": {
    "CommandLine": {
      "EmitFinalOutput": false
    }
  }
}
```

The setting only controls the final console envelope. Workflow and correlation IDs, diagnostics references, progress messages, warnings, failure details, and exit codes are preserved. Workflow execution, returned `FinalOutput`, persisted state, reports, and workflow YAML are unchanged. Null final output remains a no-op regardless of the setting.

Override the configured value for a single command with `--emit-final-output true` or `--emit-final-output false`:

```powershell
dotnet run -- start workflow example.workflow.yaml --emit-final-output true
dotnet run -- resume workflow example.workflow.yaml <workflow-instance-id> --emit-final-output true
dotnet run -- start workflow example.workflow.yaml --emit-final-output false
```

The bare `--emit-final-output` flag also enables output. An explicit command option takes precedence over environment variables and JSON settings; omitting it uses the configured value, which defaults to `true`. This override applies only to the current command and is not persisted with the workflow. If the workflow pauses, pass the option again when resuming to override that command's configured output behaviour.

Use the standard environment-variable form in container deployments:

```text
Orchestrator__CommandLine__EmitFinalOutput=false
```

For Azure Container Apps, add this entry to the existing container's `env` list under `properties.template.containers` in the **deployment** YAML:

```yaml
env:
  - name: Orchestrator__CommandLine__EmitFinalOutput
    value: "false"
```

`OrchestratorCommandLineHost.CreateBuilder` loads environment variables after JSON settings, so this environment variable overrides JSON configuration. Custom hosts must include `AddEnvironmentVariables()` in their configuration pipeline and pass that configuration to `AddOrchestratorCommandLine(configuration)`. The existing overload without configuration remains available and uses the default options unless the host configures `OrchestratorCommandLineOptions` separately.

For local debugging, leave the setting absent to retain the default output, or explicitly set it to `true`. Use `--emit-final-output true` to override an inherited environment value of `false` for one run; JSON settings do not override environment variables in the shared host. Invalid configuration boolean values are rejected when the workflow command is constructed, and invalid command-line values fail parsing before workflow execution.

## Workflow input and execution options

Both `start workflow` and `resume workflow` accept `--input` as a JSON object, comma-delimited `key=value` pairs, or a raw string. Pair values recognize JSON scalars, so boolean and numeric inputs retain their types:

```powershell
dotnet run -- start workflow DataMaintenance\idempotent.workflow.yaml --input DryRun=true --log-steps
dotnet run -- start workflow DataMaintenance\idempotent.workflow.yaml --input "DryRun=false,Limit=100,Threshold=0.5,Optional=null,Name=Beacon"
```

The following examples show the input text received by the parser, after command-line escaping:

| Input | JSON result |
| --- | --- |
| `DryRun=true` | `{"DryRun":true}` |
| `Limit=100` | `{"Limit":100}` |
| `Threshold=0.5` | `{"Threshold":0.5}` |
| `Optional=null` | `{"Optional":null}` |
| `Name=Beacon` | `{"Name":"Beacon"}` |
| `Code="100"` | `{"Code":"100"}` |
| `DryRun="true"` | `{"DryRun":"true"}` |
| `Code=00100` | `{"Code":"00100"}` |
| `Name=` | `{"Name":""}` |

Values follow JSON syntax: `true`, `false`, and `null` are lowercase; numbers use a decimal point regardless of the current culture and may use exponent notation. Values that are not valid JSON scalars remain strings, including uppercase `TRUE`, leading-zero identifiers, and empty values. Keys and values are trimmed, while whitespace inside a JSON-quoted string is preserved.

Callers that previously relied on values such as `true`, `100`, or `null` being strings must now pass them as JSON-quoted strings. Preserve the inner double quotes when escaping for your shell or launch configuration; quotes that only group a command-line argument do not force a string value. For example, this `launchSettings.json` argument supplies a boolean `DryRun` and a string `Code`:

```json
"commandLineArgs": "start workflow \"DataMaintenance\\idempotent.workflow.yaml\" --input \"DryRun=true,Code=\\\"100\\\"\" --log-steps"
```

Commas always separate pairs, including inside quoted values. Use a full JSON object for arrays, nested objects, or strings containing commas. For example, this launch configuration supplies a typed boolean and a string containing a comma:

```json
"commandLineArgs": "start workflow \"DataMaintenance\\idempotent.workflow.yaml\" --input \"{\\\"DryRun\\\":true,\\\"Name\\\":\\\"Beacon, Sydney\\\"}\" --log-steps"
```

Full JSON objects retain their supplied types. Raw inputs continue to be wrapped as strings under `Value` (for example, `--input true` produces `{"Value":"true"}`). Malformed pair lists also retain this raw-string fallback.

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
