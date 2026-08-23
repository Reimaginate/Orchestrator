using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Workflows;
using Reimaginate.Orchestrator.Common.Helpers;

namespace Reimaginate.Orchestrator.Common.Executors;

internal sealed class MapTaskExecutor(
    string id,
    string locationHint,
    WorkflowMapPlan? plan,
    JsonObject? outputMap = null,
    JsonObject? outputStashMap = null,
    string? condition = null,
    JsonObject? environment = null) : Executor<JsonObject, JsonObject>(id)
{
    public override ValueTask<JsonObject> HandleAsync(JsonObject currentObject, IWorkflowContext context, CancellationToken cancellationToken = default)
        => WorkflowTaskExecutionGuard.RunAsync(Id, locationHint, () => HandleCoreAsync(currentObject, context));

    private ValueTask<JsonObject> HandleCoreAsync(JsonObject currentObject, IWorkflowContext workflowContext)
    {
        if (!WorkflowConditionEvaluator.Evaluate(condition, currentObject, environment))
        {
            return ValueTask.FromResult((JsonObject)currentObject.DeepClone());
        }

        var mapped = WorkflowMapRuntime.Execute(
            plan ?? throw new InvalidOperationException($"Map task '{Id}' at {locationHint} does not have a compiled map plan."),
            currentObject,
            environment);
        var output = outputMap is null
            ? mapped
            : WorkflowMappingResolver.ResolveBestEffortProjection(outputMap, mapped, currentObject, mapped, workflowContext, environment)
              ?? throw new InvalidOperationException("Resolved map output was not a JSON object.");

        var resolvedOutputStash = outputStashMap is null
            ? null
            : WorkflowMappingResolver.ResolveBestEffortProjection(outputStashMap, mapped, currentObject, mapped, workflowContext, environment);

        return ValueTask.FromResult(WorkflowStashHelper.PreserveAndMergeOwnedOutputStash(currentObject, output, resolvedOutputStash));
    }
}
