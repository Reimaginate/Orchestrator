using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Workflows;
using Reimaginate.Orchestrator.Common.Constants;
using Reimaginate.Orchestrator.Common.Helpers;
using Reimaginate.Orchestrator.Common.Models;

namespace Reimaginate.Orchestrator.Common.Executors;

internal sealed class TerminateTaskExecutor(
    string id,
    string locationHint,
    string? terminalStatus,
    JsonNode? outputTemplate = null,
    string? reasonTemplate = null,
    string? condition = null,
    JsonObject? environment = null) : Executor<JsonObject, object>(id)
{
    public override ValueTask<object> HandleAsync(JsonObject currentObject, IWorkflowContext context, CancellationToken cancellationToken = default)
        => WorkflowTaskExecutionGuard.RunAsync(Id, locationHint, () => HandleCoreAsync(currentObject, context));

    private ValueTask<object> HandleCoreAsync(JsonObject currentObject, IWorkflowContext workflowContext)
    {
        if (!WorkflowConditionEvaluator.Evaluate(condition, currentObject, environment))
        {
            return ValueTask.FromResult<object>((JsonObject)currentObject.DeepClone());
        }

        var normalizedStatus = NormalizeStatus(terminalStatus);
        var finalOutput = ResolveFinalOutput(currentObject, workflowContext);
        var reason = ResolveReason(currentObject, workflowContext);

        return ValueTask.FromResult<object>(new WorkflowTerminalResult(normalizedStatus, finalOutput, reason));
    }

    private JsonObject ResolveFinalOutput(JsonObject currentObject, IWorkflowContext workflowContext)
    {
        if (outputTemplate is null)
        {
            return (JsonObject)currentObject.DeepClone();
        }

        var resolvedOutput = WorkflowMappingResolver.ResolveJsonNode(outputTemplate, currentObject, exactRootPayload: currentObject, workflowContext: workflowContext, environment: environment)
            as JsonObject
            ?? throw new InvalidOperationException($"Task '{Id}' at {locationHint} resolved terminate output to a non-object payload.");

        return WorkflowStashHelper.PreserveStash(currentObject, resolvedOutput);
    }

    private string? ResolveReason(JsonObject currentObject, IWorkflowContext workflowContext)
    {
        if (string.IsNullOrWhiteSpace(reasonTemplate))
        {
            return null;
        }

        var resolvedReason = WorkflowMappingResolver.ResolveTemplate(reasonTemplate, currentObject, exactRootPayload: currentObject, workflowContext: workflowContext, environment: environment);
        return resolvedReason switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            _ => resolvedReason.ToJsonString()
        };
    }

    private static string NormalizeStatus(string? status)
    {
        return status switch
        {
            WorkflowInstanceStatuses.Completed => WorkflowInstanceStatuses.Completed,
            WorkflowInstanceStatuses.Cancelled => WorkflowInstanceStatuses.Cancelled,
            WorkflowInstanceStatuses.Failed => WorkflowInstanceStatuses.Failed,
            _ => throw new InvalidOperationException($"Terminate task defines unsupported status '{status}'.")
        };
    }
}
