using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Workflows;
using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.Common.Diagnostics;
using Reimaginate.Orchestrator.Common.Helpers;
using Reimaginate.Orchestrator.DSL;

namespace Reimaginate.Orchestrator.Common.Executors;

internal sealed class EmitTaskExecutor(
    string id,
    string locationHint,
    IWorkflowEventEmitter workflowEventEmitter,
    EmitTaskDefinition? definition,
    JsonObject? dataMap,
    JsonObject? outputMap,
    JsonObject? outputStashMap,
    string? condition,
    bool captureErrors = false,
    JsonObject? environment = null) : Executor<JsonObject, JsonObject>(id)
{
    private readonly IWorkflowEventEmitter _workflowEventEmitter = workflowEventEmitter ?? throw new ArgumentNullException(nameof(workflowEventEmitter));
    private readonly EmitTaskDefinition _definition = definition ?? throw new ArgumentNullException(nameof(definition));

    public override ValueTask<JsonObject> HandleAsync(JsonObject currentObject, IWorkflowContext context, CancellationToken cancellationToken = default)
        => WorkflowTaskExecutionGuard.RunAsync(Id, locationHint, () => HandleCoreAsync(currentObject, context, cancellationToken));

    private async ValueTask<JsonObject> HandleCoreAsync(JsonObject currentObject, IWorkflowContext context, CancellationToken cancellationToken)
    {
        if (!WorkflowConditionEvaluator.Evaluate(condition, currentObject, environment))
        {
            return (JsonObject)currentObject.DeepClone();
        }

        try
        {
            var eventType = ResolveTemplate(_definition.EventType, currentObject, context);
            if (string.IsNullOrWhiteSpace(eventType))
            {
                throw new InvalidOperationException($"Task '{Id}' emit event type resolved to an empty value.");
            }

            var destination = ResolveTemplate(_definition.To, currentObject, context);
            if (string.IsNullOrWhiteSpace(destination))
            {
                destination = "system-events";
            }

            var data = ResolveData(dataMap, currentObject, context) ?? new JsonObject();
            var message = new WorkflowEventMessage
            {
                SpecVersion = "1.0",
                Id = ResolveTemplate(_definition.Id, currentObject, context) ?? Guid.NewGuid().ToString("N"),
                Source = ResolveTemplate(_definition.Source, currentObject, context) ?? "reimaginate.orchestrator",
                Type = eventType,
                Subject = ResolveTemplate(_definition.Subject, currentObject, context),
                Time = DateTimeOffset.UtcNow,
                DataContentType = "application/json",
                Data = data
            };

            var emitted = await _workflowEventEmitter.EmitAsync(destination, message, cancellationToken);

            var result = outputMap is null
                ? (JsonObject)currentObject.DeepClone()
                : WorkflowEventInputTransformer.Transform(new JsonObject
                {
                    ["event"] = new JsonObject
                    {
                        ["id"] = emitted.Id,
                        ["type"] = emitted.Type,
                        ["source"] = emitted.Source,
                        ["subject"] = emitted.Subject,
                        ["time"] = emitted.Time?.ToString("O"),
                        ["destination"] = destination
                    }
                }, outputMap, context, environment);
            var outputContext = new JsonObject
            {
                ["event"] = new JsonObject
                {
                    ["id"] = emitted.Id,
                    ["type"] = emitted.Type,
                    ["source"] = emitted.Source,
                    ["subject"] = emitted.Subject,
                    ["time"] = emitted.Time?.ToString("O"),
                    ["destination"] = destination
                }
            };
            var resolvedOutputStash = outputStashMap is null
                ? null
                : WorkflowEventInputTransformer.Transform(outputContext, outputStashMap, context, environment);

            if (captureErrors)
            {
                result["__error"] = null;
            }

            await WorkflowExecutionTraceContext.RecordAsync("executor.emit.completed", new JsonObject
            {
                ["taskName"] = Id,
                ["destination"] = destination,
                ["eventType"] = emitted.Type,
                ["eventId"] = emitted.Id
            }, cancellationToken);
            return WorkflowStashHelper.PreserveAndMergeOutputStash(currentObject, result, resolvedOutputStash);
        }
        catch (Exception ex) when (captureErrors)
        {
            var faultPayload = (JsonObject)currentObject.DeepClone();
            var errorEnvelope = WorkflowErrorEnvelopeFactory.Create(ex, Id);
            errorEnvelope["source"] = "emit";
            faultPayload["__error"] = errorEnvelope;
            await WorkflowExecutionTraceContext.RecordAsync("executor.emit.error_captured", new JsonObject
            {
                ["taskName"] = Id,
                ["message"] = ex.Message
            }, cancellationToken);
            return faultPayload;
        }
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
