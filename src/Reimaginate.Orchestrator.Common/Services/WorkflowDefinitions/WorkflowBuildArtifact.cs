using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Workflows;
using Reimaginate.Orchestrator.DSL;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowDefinitions;

public sealed class WorkflowBuildArtifact
{
    public Workflow Workflow { get; set; } = null!;
    public JsonObject DiagnosticsGraph { get; set; } = new();
    public WorkflowDefinition Definition { get; set; } = null!;
}
