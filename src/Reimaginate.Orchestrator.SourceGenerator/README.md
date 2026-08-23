# Reimaginate.Orchestrator.SourceGenerator

This package provides:

- the `WorkflowActionResolver` source generator
- build-transitive assets that emit workflow action catalogs for host projects

## Host project setup

1. Add a package reference to `Reimaginate.Orchestrator.SourceGenerator`, or reference `Reimaginate.Orchestrator.Common` if you want the runtime and generator together.
2. Declare one or more custom `WorkflowActionResolver` types in the host project.
3. Build the host project.

Catalog generation runs automatically during normal builds. To disable it for a
consumer project, set:

```xml
<PropertyGroup>
  <OrchestratorEmitWorkflowActionCatalog>false</OrchestratorEmitWorkflowActionCatalog>
</PropertyGroup>
```

After a successful build, the package writes a workflow action catalog to:

```text
<repo-root>/.orchestrator/workflow-actions/<HostAssembly>.json
```

If the package cannot infer a repository or solution root, it falls back to:

```text
<project>/obj/orchestrator/workflow-actions/<HostAssembly>.json
```

## Notes

- A normal `PackageReference` is sufficient when consuming the published package. `Reimaginate.Orchestrator.SourceGenerator` brings in `Reimaginate.Orchestrator.Abstractions` transitively, so the standalone package includes the resolver and workflow attributes needed by host projects.
- `Reimaginate.Orchestrator.Common` embeds these build assets transitively for the standard custom-host setup.
- Catalogs are emitted only for hosts that declare at least one custom `WorkflowActionResolver`.
- Hosts that rely only on the default `Reimaginate.Orchestrator.Common.WorkflowActionResolver` do not emit catalogs.
- Catalogs are generated for direct project references that contain `*.workflow.yaml` or `*.workflow.yml` files.
- In-proc catalog task hosting assumes `.NET SDK 10` with `MSBuild 18` or newer, including Visual Studio 2026 / VS 18+.
- Transient catalog file-lock contention is reported as a build warning so the
  main compilation can still succeed.
- The VS Code workflow add-in reads these catalogs to provide dynamic `call:` completions.
