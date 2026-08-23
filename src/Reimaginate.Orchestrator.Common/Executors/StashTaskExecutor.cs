using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Workflows;
using Reimaginate.Orchestrator.Common.Helpers;

namespace Reimaginate.Orchestrator.Common.Executors;

internal sealed class StashTaskExecutor(string id, string locationHint, JsonObject? stashMap, string? condition = null, JsonObject? environment = null) : Executor<JsonObject, JsonObject>(id)
{
    public override ValueTask<JsonObject> HandleAsync(JsonObject currentObject, IWorkflowContext context, CancellationToken cancellationToken = default)
        => WorkflowTaskExecutionGuard.RunAsync(Id, locationHint, () => HandleCoreAsync(currentObject, context));

    private ValueTask<JsonObject> HandleCoreAsync(JsonObject currentObject, IWorkflowContext workflowContext)
    {
        if (!WorkflowConditionEvaluator.Evaluate(condition, currentObject, environment))
        {
            return ValueTask.FromResult((JsonObject)currentObject.DeepClone());
        }

        var output = (JsonObject)currentObject.DeepClone();

        if (stashMap is null)
        {
            WorkflowStashHelper.MergeIntoStash(output, new JsonObject());
            return ValueTask.FromResult(output);
        }

        var resolvedValues = WorkflowMappingResolver.ResolveSequentialStashValues(
            stashMap,
            output,
            workflowContext,
            environment);

        WorkflowStashHelper.MergeIntoStash(output, resolvedValues);

        return ValueTask.FromResult(output);
    }
}
