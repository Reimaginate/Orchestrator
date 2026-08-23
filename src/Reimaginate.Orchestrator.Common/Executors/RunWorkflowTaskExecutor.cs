using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Workflows;
using Reimaginate.Orchestrator.Common.Helpers;

namespace Reimaginate.Orchestrator.Common.Executors;

internal sealed class RunWorkflowTaskExecutor(string id, string locationHint, JsonObject? inputMap = null, string? condition = null, JsonObject? environment = null)
    : Executor<JsonObject, JsonObject>(id)
{
    public override ValueTask<JsonObject> HandleAsync(JsonObject currentObject, IWorkflowContext context, CancellationToken cancellationToken = default)
        => WorkflowTaskExecutionGuard.RunAsync(Id, locationHint, () => HandleCoreAsync(currentObject, context));

    private ValueTask<JsonObject> HandleCoreAsync(JsonObject currentObject, IWorkflowContext workflowContext)
    {
        if (!WorkflowConditionEvaluator.Evaluate(condition, currentObject, environment))
        {
            var skippedOutput = (JsonObject)currentObject.DeepClone();
            skippedOutput[SwitchTaskExecutor.SelectedRouteProperty] = DoControlFlowDirectiveTaskExecutor.ExitRoute;
            return ValueTask.FromResult(skippedOutput);
        }

        var overlay = inputMap is null
            ? null
            : WorkflowEventInputTransformer.Transform(currentObject, inputMap, workflowContext, environment);

        var output = WorkflowStashHelper.CreateInlineWorkflowInput(currentObject, overlay);
        output[SwitchTaskExecutor.SelectedRouteProperty] = DoControlFlowDirectiveTaskExecutor.ContinueRoute;

        return ValueTask.FromResult(output);
    }
}
