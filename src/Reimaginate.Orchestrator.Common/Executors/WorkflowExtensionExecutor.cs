using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Agents.AI.Workflows;
using Reimaginate.Mediator;
using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.Common.Helpers;
using Reimaginate.Orchestrator.Common.Services.WorkflowDefinitions;
using Reimaginate.Orchestrator.Common.Services.WorkflowDefinitions.Compilation;
using Reimaginate.Orchestrator.DSL;

namespace Reimaginate.Orchestrator.Common.Executors;

internal sealed class WorkflowExtensionExecutor(
    string id,
    string locationHint,
    Executor<JsonObject, JsonObject> inner,
    WorkflowMetadataDefinition? workflowMetadata,
    IReadOnlyList<BoundWorkflowExtension> extensions,
    IMediator mediator,
    IWorkflowBuilderAdapterFactory workflowBuilderAdapterFactory,
    IWorkflowEventEmitter workflowEventEmitter,
    IWorkflowActionResolver workflowActionResolver,
    JsonObject? environment = null,
    IWorkflowEnvironmentProvider? workflowEnvironmentProvider = null,
    string? workflowType = null) : Executor<JsonObject, JsonObject>(id)
{
    public override ValueTask<JsonObject> HandleAsync(JsonObject currentObject, IWorkflowContext context, CancellationToken cancellationToken = default)
        => WorkflowTaskExecutionGuard.RunAsync(Id, locationHint, () => HandleCoreAsync(currentObject, context, cancellationToken));

    private async ValueTask<JsonObject> HandleCoreAsync(JsonObject currentObject, IWorkflowContext context, CancellationToken cancellationToken)
    {
        var inputSnapshot = currentObject;

        foreach (var extension in extensions)
        {
            if (ShouldExecuteHook(extension.BeforeWhen, inputSnapshot, environment))
            {
                await ExecuteHookAsync(extension, "before", extension.Before, extension.BeforePlan, inputSnapshot, inputSnapshot, null, null, context, cancellationToken);
            }
        }

        JsonObject output;
        try
        {
            // Workflow executors treat their input as immutable and return an independent
            // payload. Passing the input through avoids a redundant full-payload clone while
            // retaining the original reference for before/after/error hook snapshots.
            output = await inner.HandleAsync(currentObject, context, cancellationToken);
        }
        catch (Exception ex)
        {
            var errorConditionPayload = WorkflowErrorEnvelopeFactory.Create(ex, Id);
            for (var i = extensions.Count - 1; i >= 0; i--)
            {
                var extension = extensions[i];
                if (ShouldExecuteHook(extension.OnErrorWhen, errorConditionPayload, environment))
                {
                    await ExecuteHookAsync(extension, "onError", extension.OnError, extension.OnErrorPlan, inputSnapshot, inputSnapshot, null, ex, context, cancellationToken);
                }
            }

            throw;
        }

        for (var i = extensions.Count - 1; i >= 0; i--)
        {
            var extension = extensions[i];
            if (ShouldExecuteHook(extension.AfterWhen, output, environment))
            {
                await ExecuteHookAsync(extension, "after", extension.After, extension.AfterPlan, output, inputSnapshot, output, null, context, cancellationToken);
            }
        }

        return output;
    }

    private async Task ExecuteHookAsync(
        BoundWorkflowExtension extension,
        string hookName,
        WorkflowTaskBlockDefinition? taskBlock,
        WorkflowCompilationPlan? hookPlan,
        JsonObject rootPayload,
        JsonObject inputSnapshot,
        JsonObject? outputSnapshot,
        Exception? error,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        if (taskBlock is null || taskBlock.Do.Count == 0)
        {
            return;
        }

        if (hookPlan is null)
        {
            throw new InvalidOperationException($"Extension '{extension.Name}' {hookName} hook does not have a compiled workflow plan.");
        }

        var hookInput = BuildHookInput(rootPayload, inputSnapshot, outputSnapshot, error, context, extension.Name, hookName);

        try
        {
            var hookWorkflow = BuildHookWorkflow(hookPlan);
            await using var run = await InProcessExecution.RunStreamingAsync(hookWorkflow, input: hookInput, cancellationToken: cancellationToken);

            await foreach (var evt in run.WatchStreamAsync(cancellationToken))
            {
                if (evt is WorkflowErrorEvent workflowErrorEvent)
                {
                    throw workflowErrorEvent.Exception ?? new InvalidOperationException($"Extension '{extension.Name}' {hookName} hook failed.");
                }
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Extension '{extension.Name}' {hookName} hook failed for task '{Id}': {ex.Message}", ex);
        }
    }

    private Workflow BuildHookWorkflow(WorkflowCompilationPlan hookPlan)
    {
        var hookChannel = Channel.CreateUnbounded<JsonObject>();
        return new WorkflowPlanMaterializer(mediator, workflowBuilderAdapterFactory, hookChannel, workflowEventEmitter, workflowActionResolver, workflowEnvironmentProvider, workflowType)
            .Materialize(hookPlan);
    }

    private static bool ShouldExecuteHook(string? when, JsonObject payload, JsonObject? environment)
        => string.IsNullOrWhiteSpace(when) || WorkflowConditionEvaluator.Evaluate(when, payload, environment);

    private JsonObject BuildHookInput(
        JsonObject rootPayload,
        JsonObject inputSnapshot,
        JsonObject? outputSnapshot,
        Exception? error,
        IWorkflowContext context,
        string extensionName,
        string hookName)
    {
        var hookInput = (JsonObject)rootPayload.DeepClone();

        hookInput["workflow"] = BuildWorkflowInfo(context, extensionName, hookName);
        hookInput["task"] = new JsonObject
        {
            ["name"] = Id,
            ["location"] = locationHint,
            ["extension"] = extensionName,
            ["hook"] = hookName
        };
        hookInput["input"] = inputSnapshot.DeepClone();

        if (outputSnapshot is not null)
        {
            hookInput["output"] = outputSnapshot.DeepClone();
        }

        if (error is not null)
        {
            hookInput["error"] = WorkflowErrorEnvelopeFactory.Create(error, Id);
        }

        return hookInput;
    }

    private JsonObject BuildWorkflowInfo(IWorkflowContext context, string extensionName, string hookName)
    {
        var workflowInfo = new JsonObject
        {
            ["dsl"] = workflowMetadata?.Dsl,
            ["namespace"] = workflowMetadata?.Namespace,
            ["name"] = workflowMetadata?.Name,
            ["version"] = workflowMetadata?.Version,
            ["extension"] = extensionName,
            ["hook"] = hookName
        };

        foreach (var property in context
                     .GetType()
                     .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(property => property.CanRead && property.GetIndexParameters().Length == 0))
        {
            object? value;
            try
            {
                value = property.GetValue(context);
            }
            catch
            {
                continue;
            }

            workflowInfo[property.Name] = TryConvertToJsonNode(value);
        }

        return workflowInfo;
    }

    private static JsonNode? TryConvertToJsonNode(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case JsonNode jsonNode:
                return jsonNode.DeepClone();
            default:
                try
                {
                    return JsonSerializer.SerializeToNode(value);
                }
                catch
                {
                    return JsonValue.Create(value.ToString());
                }
        }
    }
}
