using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Workflows;
using Reimaginate.Orchestrator.Common.Helpers;

namespace Reimaginate.Orchestrator.Common.Executors;

internal sealed class PassthroughTaskExecutor(string id, string locationHint, JsonObject? inputMap = null, JsonObject? outputMap = null, JsonObject? outputStashMap = null, string? condition = null, JsonObject? environment = null) : Executor<JsonObject, JsonObject>(id)
{
    #region Execution

    public override ValueTask<JsonObject> HandleAsync(JsonObject currentObject, IWorkflowContext context, CancellationToken cancellationToken = default)
        => WorkflowTaskExecutionGuard.RunAsync(Id, locationHint, () => HandleCoreAsync(currentObject, context));

    private ValueTask<JsonObject> HandleCoreAsync(JsonObject currentObject, IWorkflowContext workflowContext)
    {
        var isInlineDoEntry = locationHint.EndsWith(".do-entry", StringComparison.OrdinalIgnoreCase);

        // No-op quickly when the step condition does not match.
        if (!WorkflowConditionEvaluator.Evaluate(condition, currentObject, environment))
        {
            var skippedOutput = (JsonObject)currentObject.DeepClone();
            if (isInlineDoEntry)
            {
                skippedOutput[SwitchTaskExecutor.SelectedRouteProperty] = DoControlFlowDirectiveTaskExecutor.ExitRoute;
            }

            return ValueTask.FromResult(skippedOutput);
        }

        var input = inputMap is null
            ? (JsonObject)currentObject.DeepClone()
            : WorkflowEventInputTransformer.Transform(currentObject, inputMap, workflowContext, environment);

        var output = outputMap is null
            ? input
            : WorkflowEventInputTransformer.Transform(input, outputMap, workflowContext, environment);

        var resolvedOutputStash = outputStashMap is null
            ? null
            : WorkflowEventInputTransformer.Transform(input, outputStashMap, workflowContext, environment);

        var mergedOutput = WorkflowStashHelper.PreserveAndMergeOutputStash(currentObject, output, resolvedOutputStash);
        if (isInlineDoEntry)
        {
            mergedOutput[SwitchTaskExecutor.SelectedRouteProperty] = DoControlFlowDirectiveTaskExecutor.ContinueRoute;
        }

        return ValueTask.FromResult(mergedOutput);
    }

    #endregion
}
