using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Workflows;
using Reimaginate.Orchestrator.Common.Diagnostics;
using Reimaginate.Orchestrator.Common.Helpers;

namespace Reimaginate.Orchestrator.Common.Executors;

internal sealed class ForEachLoopGateTaskExecutor(
    string id,
    string locationHint,
    string sourceExpression,
    string itemVariable,
    string? indexVariable,
    int? batchSize) : Executor<JsonObject, JsonObject>(id)
{
    #region Route constants

    public const string ContinueRoute = "__for_each_continue";
    public const string ExitRoute = "__for_each_exit";

    #endregion

    #region Loop state

    private const string LoopStatePropertyName = "__loopState";
    private const string StoredLoopStateKey = "state";

    #endregion

    #region Execution

    public override ValueTask<JsonObject> HandleAsync(JsonObject currentObject, IWorkflowContext context, CancellationToken cancellationToken = default)
        => WorkflowTaskExecutionGuard.RunAsync(Id, locationHint, () => HandleCoreAsync(currentObject, context, cancellationToken));

    private async ValueTask<JsonObject> HandleCoreAsync(JsonObject currentObject, IWorkflowContext context, CancellationToken cancellationToken)
    {
        var output = (JsonObject)currentObject.DeepClone();
        cancellationToken.ThrowIfCancellationRequested();

        var payloadState = EnsureLoopState(output);
        var loopState = await context.ReadOrInitStateAsync<JsonObject>(StoredLoopStateKey, static () => new JsonObject(), Id, cancellationToken)
            ?? new JsonObject();

        SyncState(payloadState, loopState);

        var sourceArray = ResolveLoopSourceArray(output, payloadState, loopState);
        if (sourceArray is null)
        {
            output[SwitchTaskExecutor.SelectedRouteProperty] = ExitRoute;
            await WorkflowExecutionTraceContext.RecordAsync("executor.foreach.route_selected", new JsonObject
            {
                ["taskName"] = Id,
                ["routeTaskName"] = ExitRoute,
                ["reason"] = "source_missing",
                ["sourceExpression"] = sourceExpression
            }, cancellationToken);
            await context.QueueClearScopeAsync(Id, cancellationToken);
            return output;
        }

        var currentIndex = Math.Max(payloadState["index"]?.GetValue<int>() ?? -1, loopState["index"]?.GetValue<int>() ?? -1);
        var nextIndex = currentIndex + 1;
        var iterationCount = ResolveIterationCount(sourceArray);
        cancellationToken.ThrowIfCancellationRequested();

        if (nextIndex >= iterationCount)
        {
            var lastIndex = loopState["index"]?.GetValue<int>() ?? -1;
            if (lastIndex >= 0)
            {
                payloadState["index"] = lastIndex;
            }

            if (!string.IsNullOrWhiteSpace(indexVariable) && lastIndex >= 0)
            {
                output[indexVariable!] = lastIndex;
            }

            if (lastIndex >= 0 && lastIndex < iterationCount)
            {
                output[itemVariable] = BuildIterationValue(sourceArray, lastIndex);
            }

            output[SwitchTaskExecutor.SelectedRouteProperty] = ExitRoute;
            await WorkflowExecutionTraceContext.RecordAsync("executor.foreach.route_selected", new JsonObject
            {
                ["taskName"] = Id,
                ["routeTaskName"] = ExitRoute,
                ["iterationIndex"] = lastIndex,
                ["iterationCount"] = iterationCount
            }, cancellationToken);
            await context.QueueClearScopeAsync(Id, cancellationToken);
            return output;
        }

        output[itemVariable] = BuildIterationValue(sourceArray, nextIndex);
        if (!string.IsNullOrWhiteSpace(indexVariable))
        {
            output[indexVariable!] = nextIndex;
        }

        payloadState["index"] = nextIndex;
        loopState["index"] = nextIndex;
        output[SwitchTaskExecutor.SelectedRouteProperty] = ContinueRoute;
        await WorkflowExecutionTraceContext.RecordAsync("executor.foreach.route_selected", new JsonObject
        {
            ["taskName"] = Id,
            ["routeTaskName"] = ContinueRoute,
            ["iterationIndex"] = nextIndex,
            ["iterationCount"] = iterationCount,
            ["itemVariable"] = itemVariable,
            ["indexVariable"] = indexVariable
        }, cancellationToken);

        await context.QueueStateUpdateAsync(StoredLoopStateKey, loopState, Id, cancellationToken);

        return output;
    }

    #endregion

    #region Source resolution

    private JsonArray? ResolveLoopSourceArray(JsonObject payload, JsonObject payloadState, JsonObject loopState)
    {
        if (loopState["sourceItems"] is JsonArray cachedSourceItems)
        {
            var cachedSnapshot = cachedSourceItems.DeepClone() as JsonArray;
            if (cachedSnapshot is null)
            {
                return null;
            }

            payloadState["sourceItems"] = cachedSnapshot.DeepClone();
            loopState["sourceItems"] = cachedSnapshot.DeepClone();
            return cachedSnapshot;
        }

        if (payloadState["sourceItems"] is JsonArray persistedSourceItems)
        {
            var persistedSnapshot = persistedSourceItems.DeepClone() as JsonArray;
            if (persistedSnapshot is null)
            {
                return null;
            }

            loopState["sourceItems"] = persistedSnapshot.DeepClone();
            return persistedSnapshot;
        }

        var sourceNode = JsonPath.SelectToken(payload, sourceExpression);
        if (sourceNode is not JsonArray sourceArray)
        {
            return null;
        }

        var snapshot = sourceArray.DeepClone() as JsonArray;
        if (snapshot is null)
        {
            return null;
        }

        loopState["sourceItems"] = snapshot.DeepClone();
        payloadState["sourceItems"] = snapshot.DeepClone();
        return snapshot;
    }

    private int ResolveIterationCount(JsonArray sourceArray)
    {
        if (batchSize is not > 0)
        {
            return sourceArray.Count;
        }

        return (sourceArray.Count + batchSize.Value - 1) / batchSize.Value;
    }

    private JsonNode? BuildIterationValue(JsonArray sourceArray, int iterationIndex)
    {
        if (batchSize is not > 0)
        {
            return sourceArray[iterationIndex]?.DeepClone();
        }

        var startIndex = iterationIndex * batchSize.Value;
        if (startIndex < 0 || startIndex >= sourceArray.Count)
        {
            return null;
        }

        var chunk = new JsonArray();
        var endExclusive = Math.Min(startIndex + batchSize.Value, sourceArray.Count);
        for (var index = startIndex; index < endExclusive; index++)
        {
            chunk.Add(sourceArray[index]?.DeepClone());
        }

        return chunk;
    }

    #endregion

    #region Payload state helpers

    private static void SyncState(JsonObject payloadState, JsonObject loopState)
    {
        if (payloadState["index"] is null && loopState["index"] is not null)
        {
            payloadState["index"] = loopState["index"]?.DeepClone();
        }

        if (payloadState["sourceItems"] is null && loopState["sourceItems"] is not null)
        {
            payloadState["sourceItems"] = loopState["sourceItems"]?.DeepClone();
        }
    }

    private JsonObject EnsureLoopState(JsonObject payload)
    {
        if (payload[LoopStatePropertyName] is not JsonObject allLoopState)
        {
            allLoopState = new JsonObject();
            payload[LoopStatePropertyName] = allLoopState;
        }

        if (allLoopState[Id] is not JsonObject nodeState)
        {
            nodeState = new JsonObject();
            allLoopState[Id] = nodeState;
        }

        return nodeState;
    }

    #endregion
}
