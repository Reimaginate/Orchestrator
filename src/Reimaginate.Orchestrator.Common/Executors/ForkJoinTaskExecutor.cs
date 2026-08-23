using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Workflows;
using Reimaginate.Orchestrator.Common.Diagnostics;
using Reimaginate.Orchestrator.Common.Helpers;

namespace Reimaginate.Orchestrator.Common.Executors;

internal sealed class ForkJoinTaskExecutor(string id, string locationHint, int requiredArrivals, JsonObject? outputMap = null, JsonObject? outputStashMap = null, string? condition = null, JsonObject? environment = null) : Executor<JsonObject, JsonObject>(id)
{
    private const string ForkStatePropertyName = "__forkState";
    private static readonly ConcurrentDictionary<string, int> ArrivalCountsByFork = new(StringComparer.Ordinal);

    public override ValueTask<JsonObject> HandleAsync(JsonObject currentObject, IWorkflowContext context, CancellationToken cancellationToken = default)
        => WorkflowTaskExecutionGuard.RunAsync(Id, locationHint, () => HandleCoreAsync(currentObject, context, cancellationToken));

    private ValueTask<JsonObject> HandleCoreAsync(JsonObject currentObject, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        if (!WorkflowConditionEvaluator.Evaluate(condition, currentObject, environment))
        {
            return ValueTask.FromResult((JsonObject)currentObject.DeepClone());
        }

        var output = (JsonObject)currentObject.DeepClone();
        var forkState = EnsureForkState(output);
        var key = BuildForkStateKey(output, context);

        var arrivals = ArrivalCountsByFork.AddOrUpdate(key, 1, (_, current) => current + 1);
        forkState["arrivals"] = arrivals;
        forkState["requiredArrivals"] = requiredArrivals;

        if (arrivals < requiredArrivals)
        {
            var waitOutput = BuildMappedOutput(currentObject, output, context);
            waitOutput[SwitchTaskExecutor.SelectedRouteProperty] = WaitRoute;
            _ = WorkflowExecutionTraceContext.RecordAsync("executor.fork.route_selected", new JsonObject
            {
                ["taskName"] = Id,
                ["routeTaskName"] = WaitRoute,
                ["arrivals"] = arrivals,
                ["requiredArrivals"] = requiredArrivals
            }, cancellationToken);
            return ValueTask.FromResult(waitOutput);
        }

        ArrivalCountsByFork.TryRemove(key, out _);

        var transformed = BuildMappedOutput(currentObject, output, context);
        transformed[SwitchTaskExecutor.SelectedRouteProperty] = ContinueRoute;
        _ = WorkflowExecutionTraceContext.RecordAsync("executor.fork.route_selected", new JsonObject
        {
            ["taskName"] = Id,
            ["routeTaskName"] = ContinueRoute,
            ["arrivals"] = arrivals,
            ["requiredArrivals"] = requiredArrivals
        }, cancellationToken);
        return ValueTask.FromResult(transformed);
    }

    private JsonObject BuildMappedOutput(JsonObject currentObject, JsonObject output, IWorkflowContext context)
    {
        var transformed = outputMap is null
            ? (JsonObject)output.DeepClone()
            : WorkflowEventInputTransformer.Transform(output, outputMap, context, environment);
        var resolvedOutputStash = outputStashMap is null
            ? null
            : WorkflowEventInputTransformer.Transform(output, outputStashMap, context, environment);

        return WorkflowStashHelper.PreserveAndMergeOutputStash(currentObject, transformed, resolvedOutputStash);
    }

    private string BuildForkStateKey(JsonObject payload, IWorkflowContext workflowContext)
    {
        var workflowInstanceId = GetWorkflowInstanceId(payload, workflowContext);
        return $"{workflowInstanceId}::{Id}";
    }

    private static string GetWorkflowInstanceId(JsonObject payload, IWorkflowContext workflowContext)
    {
        var metadata = WorkflowRuntimeMetadataResolver.Resolve(workflowContext);
        if (!string.IsNullOrWhiteSpace(metadata.WorkflowInstanceId))
        {
            return metadata.WorkflowInstanceId;
        }

        if (payload["workflowInstanceId"]?.GetValue<string>() is { } payloadWorkflowInstanceId
            && !string.IsNullOrWhiteSpace(payloadWorkflowInstanceId))
        {
            return payloadWorkflowInstanceId;
        }

        return "__unknown_workflow_instance";
    }

    private JsonObject EnsureForkState(JsonObject payload)
    {
        if (payload[ForkStatePropertyName] is not JsonObject allForkState)
        {
            allForkState = new JsonObject();
            payload[ForkStatePropertyName] = allForkState;
        }

        if (allForkState[Id] is not JsonObject nodeState)
        {
            nodeState = new JsonObject();
            allForkState[Id] = nodeState;
        }

        return nodeState;
    }

    public const string ContinueRoute = "__fork_continue";
    public const string WaitRoute = "__fork_wait";
}
