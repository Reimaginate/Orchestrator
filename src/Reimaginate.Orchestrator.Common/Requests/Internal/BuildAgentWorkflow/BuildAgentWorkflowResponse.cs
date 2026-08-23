using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Workflows;
using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.DSL;

namespace Reimaginate.Orchestrator.Common.Requests.Internal.BuildAgentWorkflow;

public class BuildAgentWorkflowResponse : Result
{
    public Workflow Workflow { get; set; } = null!;
    public JsonObject DiagnosticsGraph { get; set; } = new();
    public WorkflowDefinition Definition { get; set; } = null!;
}
