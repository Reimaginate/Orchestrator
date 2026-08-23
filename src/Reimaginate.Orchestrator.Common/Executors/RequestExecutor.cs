using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Workflows;
using Reimaginate.Mediator;
using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.Common.Diagnostics;
using Reimaginate.Orchestrator.Common.Helpers;

namespace Reimaginate.Orchestrator.Common.Executors;

public class RequestExecutor<TRequest, TResponse>(string id, string locationHint, IMediator mediator, JsonObject? mapIn, JsonObject? mapOut, JsonObject? outputStashMap, string? condition, bool captureErrors = false, JsonSerializerOptions? jsonOptions = null, JsonObject? environment = null)
    : Executor<JsonObject, JsonObject>(id) where TRequest : IRequest<TResponse> where TResponse : class
{
    private static readonly JsonSerializerOptions CaseSensitiveJsonOptions = new();

    private readonly JsonSerializerOptions _jsonOptions = CreateRequestSerializerOptions(jsonOptions);

    public override ValueTask<JsonObject> HandleAsync(JsonObject currentObject, IWorkflowContext context, CancellationToken cancellationToken = default)
        => WorkflowTaskExecutionGuard.RunAsync(Id, locationHint, () => HandleCoreAsync(currentObject, context, cancellationToken));

    private async ValueTask<JsonObject> HandleCoreAsync(JsonObject currentObject, IWorkflowContext workflowContext, CancellationToken cancellationToken)
    {
        if (!WorkflowConditionEvaluator.Evaluate(condition, currentObject, environment))
        {
            return (JsonObject)currentObject.DeepClone();
        }

        JsonObject requestObject;
        if (mapIn is not null)
        {
            requestObject = WorkflowMappingResolver.ResolveBestEffortProjection(mapIn, currentObject, exactRootPayload: currentObject, workflowContext: workflowContext, environment: environment)
                ?? throw new Exception("Resolved input map was not a JSON object.");
        }
        else
        {
            requestObject = (JsonObject)currentObject.DeepClone();
        }

        CoerceRequestObjectToRequestShape(requestObject);

        try
        {
            var req = DeserializeRequest(requestObject, _jsonOptions);

            var (response, ex) = await mediator.TrySend(req, cancellationToken);
            if (response is null || ex is not null)
            {
                throw ex ?? new Exception("Error occurred while processing request");
            }

            if (response is ResultBase result && !result.Success)
            {
                throw new Exception(result.FailureReason ?? $"{typeof(TRequest).Name} request failed.");
            }

            var responseNode = JsonSerializer.SerializeToNode(response, CaseSensitiveJsonOptions) as JsonObject
                               ?? throw new Exception("Response did not serialize to a JSON object.");

            JsonObject ret;
            if (mapOut is not null)
            {
                ret = WorkflowMappingResolver.ResolveBestEffortProjection(mapOut, responseNode, currentObject, responseNode, workflowContext, environment)
                    ?? throw new Exception("Resolved output map was not a JSON object.");
                ret = WorkflowStashHelper.OverlayOutputOnPayload(currentObject, ret);
            }
            else
            {
                ret = (JsonObject)responseNode.DeepClone();
            }

            var resolvedOutputStash = outputStashMap is null
                ? null
                : WorkflowMappingResolver.ResolveBestEffortProjection(outputStashMap, responseNode, currentObject, responseNode, workflowContext, environment);

            if (captureErrors)
            {
                ret["__error"] = null;
            }

            await WorkflowExecutionTraceContext.RecordAsync("executor.call.completed", new JsonObject
            {
                ["taskName"] = Id,
                ["requestType"] = typeof(TRequest).FullName,
                ["responseType"] = typeof(TResponse).FullName
            }, cancellationToken);
            return WorkflowStashHelper.PreserveAndMergeOwnedOutputStash(currentObject, ret, resolvedOutputStash);
        }
        catch (Exception ex) when (captureErrors)
        {
            var faultPayload = (JsonObject)currentObject.DeepClone();
            var errorEnvelope = WorkflowErrorEnvelopeFactory.Create(ex, Id);
            errorEnvelope["source"] = "call";
            faultPayload["__error"] = errorEnvelope;
            await WorkflowExecutionTraceContext.RecordAsync("executor.call.error_captured", new JsonObject
            {
                ["taskName"] = Id,
                ["requestType"] = typeof(TRequest).FullName,
                ["message"] = ex.Message
            }, cancellationToken);

            return faultPayload;
        }
    }

    internal static TRequest DeserializeRequest(JsonObject requestObject, JsonSerializerOptions jsonOptions)
        => requestObject.Deserialize<TRequest>(jsonOptions)
           ?? throw new Exception("Failed to deserialize request.");

    internal static JsonSerializerOptions CreateRequestSerializerOptions(JsonSerializerOptions? jsonOptions = null)
    {
        var requestOptions = jsonOptions is null
            ? new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            : new JsonSerializerOptions(jsonOptions);
        requestOptions.Converters.Insert(0, CaseSensitiveJsonNodeConverterFactory.Instance);
        return requestOptions;
    }

    private static void CoerceRequestObjectToRequestShape(JsonObject requestObject)
    {
        foreach (var property in typeof(TRequest).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.PropertyType != typeof(string)
                || !property.CanWrite
                || !requestObject.TryGetPropertyValue(property.Name, out var value)
                || value is not JsonValue jsonValue
                || jsonValue.TryGetValue<string>(out _))
            {
                continue;
            }

            requestObject[property.Name] = JsonValue.Create(value.ToJsonString());
        }
    }
}
