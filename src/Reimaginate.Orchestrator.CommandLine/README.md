# Reimaginate.Orchestrator.CommandLine

Orchestrator is Reimaginate's opinionated YAML workflow and durability layer built on the [Microsoft Agent Framework Workflows runtime](https://github.com/microsoft/agent-framework).

Reusable command-line commands and host wiring for Orchestrator workflow applications.

Typical usage:

1. Reference `Reimaginate.Orchestrator.CommandLine`.
2. Reference `Reimaginate.Orchestrator.Common`.
3. Define a partial workflow action resolver in the host project.
4. Register `AddOrchestratorCommandLine<...>(configuration)` and the generated resolver type.

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
