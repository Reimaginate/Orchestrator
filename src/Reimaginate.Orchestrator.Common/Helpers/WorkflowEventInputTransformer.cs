using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Workflows;

namespace Reimaginate.Orchestrator.Common.Helpers;

public static class WorkflowEventInputTransformer
{
    public static JsonObject Transform(JsonObject payload, JsonObject? inputTemplate, IWorkflowContext? workflowContext = null, JsonObject? environment = null)
        => WorkflowMappingResolver.Transform(payload, inputTemplate, workflowContext, environment);
}
