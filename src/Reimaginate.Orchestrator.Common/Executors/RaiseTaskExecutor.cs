using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Workflows;
using Reimaginate.Orchestrator.Common.Helpers;

namespace Reimaginate.Orchestrator.Common.Executors;

internal sealed class RaiseTaskExecutor(
    string id,
    string locationHint,
    string? errorType,
    string? message,
    JsonObject? data,
    string? condition,
    bool captureErrors = false,
    JsonObject? environment = null) : Executor<JsonObject, JsonObject>(id)
{
    public override ValueTask<JsonObject> HandleAsync(JsonObject currentObject, IWorkflowContext context, CancellationToken cancellationToken = default)
        => WorkflowTaskExecutionGuard.RunAsync(Id, locationHint, () => HandleCoreAsync(currentObject, context, cancellationToken));

    private ValueTask<JsonObject> HandleCoreAsync(JsonObject currentObject, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(condition) && !WorkflowConditionEvaluator.Evaluate(condition, currentObject, environment))
        {
            return ValueTask.FromResult((JsonObject)currentObject.DeepClone());
        }

        var error = ResolveTemplate(errorType, currentObject, context) ?? "WorkflowRaiseError";
        var resolvedMessage = ResolveTemplate(message, currentObject, context);
        var resolvedData = ResolveData(data, currentObject, context);

        var exception = new WorkflowRaisedException(error, resolvedMessage, Id, resolvedData);
        if (!captureErrors)
        {
            throw exception;
        }

        var faultPayload = (JsonObject)currentObject.DeepClone();
        faultPayload["__error"] = WorkflowErrorEnvelopeFactory.Create(exception, Id);
        return ValueTask.FromResult(faultPayload);
    }

    private string? ResolveTemplate(string? value, JsonObject context, IWorkflowContext workflowContext)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var resolved = WorkflowMappingResolver.ResolveTemplate(value, context, exactRootPayload: context, workflowContext: workflowContext, environment: environment);
        return resolved switch
        {
            null => null,
            JsonValue jsonValue when jsonValue.TryGetValue<string>(out var text) => text,
            _ => resolved.ToJsonString()
        };
    }

    private JsonObject? ResolveData(JsonObject? templateData, JsonObject context, IWorkflowContext workflowContext)
    {
        if (templateData is null)
        {
            return null;
        }

        var resolved = new JsonObject();
        foreach (var (key, value) in templateData)
        {
            resolved[key] = value switch
            {
                JsonValue jsonValue when jsonValue.TryGetValue<string>(out var rawText) => WorkflowMappingResolver.ResolveTemplate(rawText, context, exactRootPayload: context, workflowContext: workflowContext, environment: environment),
                JsonNode jsonNode => jsonNode.DeepClone(),
                _ => null
            };
        }

        return resolved;
    }
}
