using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Workflows;
using Reimaginate.Orchestrator.Common.Diagnostics;
using Reimaginate.Orchestrator.Common.Helpers;

namespace Reimaginate.Orchestrator.Common.Executors;

internal sealed class LoopGateTaskExecutor(
    string id,
    string locationHint,
    string condition,
    LoopGuardPolicy policy,
    JsonObject? environment = null) : Executor<JsonObject, JsonObject>(id)
{
    #region Route constants

    public const string ContinueRoute = "__loop_continue";
    public const string ExitRoute = "__loop_exit";

    #endregion

    #region Guard state

    private const string LoopStatePropertyName = "__loopState";
    private const string StoredLoopStateKey = "state";

    #endregion

    #region Execution

    public override ValueTask<JsonObject> HandleAsync(JsonObject currentObject, IWorkflowContext context, CancellationToken cancellationToken = default)
        => WorkflowTaskExecutionGuard.RunAsync(Id, locationHint, () => HandleCoreAsync(currentObject, context, cancellationToken));

    private async ValueTask<JsonObject> HandleCoreAsync(JsonObject currentObject, IWorkflowContext context, CancellationToken cancellationToken)
    {
        var output = (JsonObject)currentObject.DeepClone();
        if (cancellationToken.IsCancellationRequested)
        {
            if (policy.CancelMode == LoopGuardCancelMode.Exit)
            {
                output[SwitchTaskExecutor.SelectedRouteProperty] = ExitRoute;
                await WorkflowExecutionTraceContext.RecordAsync("executor.loop.route_selected", new JsonObject
                {
                    ["taskName"] = Id,
                    ["routeTaskName"] = ExitRoute,
                    ["reason"] = "cancelled"
                }, CancellationToken.None);
                await context.QueueClearScopeAsync(Id, CancellationToken.None);
                return output;
            }

            await WorkflowExecutionTraceContext.RecordAsync("executor.loop.guard_failed", new JsonObject
            {
                ["taskName"] = Id,
                ["reason"] = WorkflowLoopGuardFailureReason.Cancelled.ToString(),
                ["iterations"] = 0
            }, CancellationToken.None);
            await context.QueueClearScopeAsync(Id, CancellationToken.None);
            throw new WorkflowLoopGuardException(
                Id,
                WorkflowLoopGuardFailureReason.Cancelled,
                "loop cancelled by cancellation token",
                0,
                0,
                null,
                policy.Timeout,
                policy.CancelMode);
        }

        var payloadState = EnsureLoopState(output);
        var loopState = await context.ReadOrInitStateAsync<JsonObject>(StoredLoopStateKey, static () => new JsonObject(), Id, cancellationToken)
            ?? new JsonObject();

        SyncState(payloadState, loopState);

        var currentFingerprint = ComputePayloadHash(output);
        var startedAtUtc = ResolveStartedAtUtc(payloadState, loopState);
        var previousIterations = Math.Max(ReadInt(payloadState, "iterations"), ReadInt(loopState, "iterations"));
        var previousPayloadHash = payloadState["lastPayloadHash"]?.GetValue<string>()
            ?? loopState["lastPayloadHash"]?.GetValue<string>();
        var previousRepeatedPayloadCount = Math.Max(ReadInt(payloadState, "repeatedPayloadCount"), ReadInt(loopState, "repeatedPayloadCount"));

        var iterations = previousIterations + 1;
        var repeatedPayloadCount = string.Equals(previousPayloadHash, currentFingerprint, StringComparison.Ordinal)
            ? previousRepeatedPayloadCount + 1
            : 1;

        payloadState["iterations"] = iterations;
        payloadState["lastPayloadHash"] = currentFingerprint;
        payloadState["repeatedPayloadCount"] = repeatedPayloadCount;
        payloadState["startedAtUtc"] = startedAtUtc;

        loopState["iterations"] = iterations;
        loopState["lastPayloadHash"] = currentFingerprint;
        loopState["repeatedPayloadCount"] = repeatedPayloadCount;
        loopState["startedAtUtc"] = startedAtUtc;

        if (iterations > policy.MaxIterations)
        {
            // Fail fast when runaway loops exceed configured hard limits.
            await WorkflowExecutionTraceContext.RecordAsync("executor.loop.guard_failed", new JsonObject
            {
                ["taskName"] = Id,
                ["reason"] = WorkflowLoopGuardFailureReason.MaxIterationsExceeded.ToString(),
                ["iterations"] = iterations,
                ["maxIterations"] = policy.MaxIterations
            }, cancellationToken);
            await context.QueueClearScopeAsync(Id, cancellationToken);
            throw new WorkflowLoopGuardException(
                Id,
                WorkflowLoopGuardFailureReason.MaxIterationsExceeded,
                $"loop exceeded max iterations ({policy.MaxIterations})",
                iterations,
                repeatedPayloadCount,
                policy.MaxIterations,
                policy.Timeout,
                policy.CancelMode);
        }

        if (repeatedPayloadCount > policy.MaxRepeatedPayloads)
        {
            // Treat repeated identical payloads as probable cycles.
            await WorkflowExecutionTraceContext.RecordAsync("executor.loop.guard_failed", new JsonObject
            {
                ["taskName"] = Id,
                ["reason"] = WorkflowLoopGuardFailureReason.CycleDetected.ToString(),
                ["iterations"] = iterations,
                ["repeatedPayloadCount"] = repeatedPayloadCount,
                ["maxRepeatedPayloads"] = policy.MaxRepeatedPayloads
            }, cancellationToken);
            await context.QueueClearScopeAsync(Id, cancellationToken);
            throw new WorkflowLoopGuardException(
                Id,
                WorkflowLoopGuardFailureReason.CycleDetected,
                $"loop detected a potential cycle after {policy.MaxRepeatedPayloads} repeated payloads",
                iterations,
                repeatedPayloadCount,
                policy.MaxRepeatedPayloads,
                policy.Timeout,
                policy.CancelMode);
        }

        if (policy.Timeout is { } timeout && DateTimeOffset.UtcNow - startedAtUtc > timeout)
        {
            await WorkflowExecutionTraceContext.RecordAsync("executor.loop.guard_failed", new JsonObject
            {
                ["taskName"] = Id,
                ["reason"] = WorkflowLoopGuardFailureReason.TimeoutExceeded.ToString(),
                ["iterations"] = iterations,
                ["timeoutMs"] = timeout.TotalMilliseconds
            }, cancellationToken);
            await context.QueueClearScopeAsync(Id, cancellationToken);
            throw new WorkflowLoopGuardException(
                Id,
                WorkflowLoopGuardFailureReason.TimeoutExceeded,
                $"loop exceeded timeout of {timeout.TotalMilliseconds}ms",
                iterations,
                repeatedPayloadCount,
                null,
                timeout,
                policy.CancelMode);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            if (policy.CancelMode == LoopGuardCancelMode.Exit)
            {
                output[SwitchTaskExecutor.SelectedRouteProperty] = ExitRoute;
                await WorkflowExecutionTraceContext.RecordAsync("executor.loop.route_selected", new JsonObject
                {
                    ["taskName"] = Id,
                    ["routeTaskName"] = ExitRoute,
                    ["reason"] = "cancelled"
                }, cancellationToken);
                await context.QueueClearScopeAsync(Id, cancellationToken);
                return output;
            }

            await WorkflowExecutionTraceContext.RecordAsync("executor.loop.guard_failed", new JsonObject
            {
                ["taskName"] = Id,
                ["reason"] = WorkflowLoopGuardFailureReason.Cancelled.ToString(),
                ["iterations"] = iterations
            }, cancellationToken);
            await context.QueueClearScopeAsync(Id, cancellationToken);
            throw new WorkflowLoopGuardException(
                Id,
                WorkflowLoopGuardFailureReason.Cancelled,
                "loop cancelled by cancellation token",
                iterations,
                repeatedPayloadCount,
                null,
                policy.Timeout,
                policy.CancelMode);
        }

        var shouldContinue = WorkflowConditionEvaluator.Evaluate(condition, output, environment);
        output[SwitchTaskExecutor.SelectedRouteProperty] = shouldContinue ? ContinueRoute : ExitRoute;
        await WorkflowExecutionTraceContext.RecordAsync("executor.loop.route_selected", new JsonObject
        {
            ["taskName"] = Id,
            ["routeTaskName"] = shouldContinue ? ContinueRoute : ExitRoute,
            ["condition"] = condition,
            ["iterations"] = iterations
        }, cancellationToken);

        if (!shouldContinue)
        {
            await context.QueueClearScopeAsync(Id, cancellationToken);
            return output;
        }

        await context.QueueStateUpdateAsync(StoredLoopStateKey, loopState, Id, cancellationToken);

        return output;
    }

    #endregion

    #region Payload metadata helpers

    private static void SyncState(JsonObject payloadState, JsonObject loopState)
    {
        if (payloadState["iterations"] is null && loopState["iterations"] is not null)
        {
            payloadState["iterations"] = loopState["iterations"]?.DeepClone();
        }

        if (payloadState["lastPayloadHash"] is null && loopState["lastPayloadHash"] is not null)
        {
            payloadState["lastPayloadHash"] = loopState["lastPayloadHash"]?.DeepClone();
        }

        if (payloadState["repeatedPayloadCount"] is null && loopState["repeatedPayloadCount"] is not null)
        {
            payloadState["repeatedPayloadCount"] = loopState["repeatedPayloadCount"]?.DeepClone();
        }

        if (payloadState["startedAtUtc"] is null && loopState["startedAtUtc"] is not null)
        {
            payloadState["startedAtUtc"] = loopState["startedAtUtc"]?.DeepClone();
        }
    }

    private static DateTimeOffset ResolveStartedAtUtc(JsonObject payloadState, JsonObject loopState)
    {
        if (payloadState["startedAtUtc"]?.GetValue<DateTimeOffset>() is { } payloadStartedAtUtc)
        {
            return payloadStartedAtUtc;
        }

        if (loopState["startedAtUtc"]?.GetValue<DateTimeOffset>() is { } storedStartedAtUtc)
        {
            return storedStartedAtUtc;
        }

        return DateTimeOffset.UtcNow;
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

    #region Guard calculations

    private static string ComputePayloadHash(JsonObject payload)
    {
        var payloadForHash = (JsonObject)payload.DeepClone();
        payloadForHash.Remove(LoopStatePropertyName);
        payloadForHash.Remove(SwitchTaskExecutor.SelectedRouteProperty);

        var canonical = payloadForHash.ToJsonString();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes);
    }

    private static int ReadInt(JsonObject loopState, string propertyName)
    {
        return loopState[propertyName]?.GetValue<int>() ?? 0;
    }

    #endregion
}
