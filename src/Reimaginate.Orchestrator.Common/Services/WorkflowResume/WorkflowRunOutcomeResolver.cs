using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Reimaginate.Orchestrator.Common.Constants;
using Reimaginate.Orchestrator.Common.Models;
using Reimaginate.Orchestrator.DSL;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowResume;

internal static class WorkflowRunOutcomeResolver
{
    internal sealed record WorkflowRunOutcome(
        string Status,
        CheckpointInfo? Checkpoint,
        IReadOnlyCollection<string> ActiveHaltingTaskNames);

    internal static async Task<WorkflowRunOutcome> ResolveAsync(
        JsonCheckpointStore? store,
        WorkflowDefinition definition,
        CheckpointInfo? observedCheckpoint,
        string superStepStatus,
        bool sawWorkflowOutput,
        WorkflowTerminalResult? terminalResult,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var traversedCheckpoints = observedCheckpoint is null
            ? []
            : await CollectCheckpointChainAsync(
                store ?? throw new InvalidOperationException("A checkpoint store is required when resolving an observed checkpoint."),
                observedCheckpoint,
                definition,
                cancellationToken);

        var latestCheckpoint = traversedCheckpoints
            .OrderByDescending(x => x.Depth)
            .ThenByDescending(x => x.Checkpoint.CheckpointId, StringComparer.Ordinal)
            .FirstOrDefault()
            ?.Checkpoint
            ?? observedCheckpoint;

        var latestHaltingCheckpoint = traversedCheckpoints
            .Where(x => x.ActiveHaltingTaskNames.Count > 0)
            .OrderByDescending(x => x.Depth)
            .ThenByDescending(x => x.Checkpoint.CheckpointId, StringComparer.Ordinal)
            .FirstOrDefault();

        if (string.Equals(superStepStatus, WorkflowInstanceStatuses.Failed, StringComparison.OrdinalIgnoreCase))
        {
            return new WorkflowRunOutcome(
                WorkflowInstanceStatuses.Failed,
                latestCheckpoint,
                []);
        }

        if (latestHaltingCheckpoint is not null)
        {
            return new WorkflowRunOutcome(
                WorkflowInstanceStatuses.Suspended,
                latestHaltingCheckpoint.Checkpoint,
                latestHaltingCheckpoint.ActiveHaltingTaskNames);
        }

        if (terminalResult is not null)
        {
            return new WorkflowRunOutcome(
                terminalResult.Status,
                latestCheckpoint,
                []);
        }

        if (sawWorkflowOutput)
        {
            return new WorkflowRunOutcome(
                WorkflowInstanceStatuses.Completed,
                latestCheckpoint,
                []);
        }

        return new WorkflowRunOutcome(
            WorkflowInstanceStatuses.Suspended,
            latestCheckpoint,
            []);
    }

    private static async Task<List<CheckpointAnalysis>> CollectCheckpointChainAsync(
        JsonCheckpointStore store,
        CheckpointInfo rootCheckpoint,
        WorkflowDefinition definition,
        CancellationToken cancellationToken)
    {
        var analyses = new List<CheckpointAnalysis>();
        var haltingTaskNames = GetHaltingTaskNames(definition);

        await TraverseAsync(store, rootCheckpoint, depth: 0, haltingTaskNames, analyses, cancellationToken);
        return analyses;
    }

    private static async Task TraverseAsync(
        JsonCheckpointStore store,
        CheckpointInfo checkpoint,
        int depth,
        HaltingTaskNames haltingTaskNames,
        ICollection<CheckpointAnalysis> analyses,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var payload = await store.RetrieveCheckpointAsync(checkpoint.SessionId, checkpoint);
            var activeHaltingTaskNames = ResolveActiveHaltingTaskNames(payload, haltingTaskNames);
            analyses.Add(new CheckpointAnalysis(checkpoint, depth, activeHaltingTaskNames));
        }
        catch
        {
            analyses.Add(new CheckpointAnalysis(checkpoint, depth, []));
        }

        var children = await store.RetrieveIndexAsync(checkpoint.SessionId, checkpoint);
        foreach (var child in children)
        {
            await TraverseAsync(store, child, depth + 1, haltingTaskNames, analyses, cancellationToken);
        }
    }

    private static HaltingTaskNames GetHaltingTaskNames(WorkflowDefinition definition)
    {
        var listenTaskNames = new HashSet<string>(StringComparer.Ordinal);
        var waitTaskNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (taskName, taskDefinition) in EnumerateTasks(definition.Do))
        {
            if (taskDefinition is ListenTaskDefinition)
            {
                listenTaskNames.Add(taskName);
            }

            if (taskDefinition is WaitTaskDefinition)
            {
                waitTaskNames.Add(taskName);
            }
        }

        return new HaltingTaskNames(listenTaskNames, waitTaskNames);
    }

    private static IReadOnlyCollection<string> ResolveActiveHaltingTaskNames(JsonElement checkpointPayload, HaltingTaskNames haltingTaskNames)
    {
        var activeTaskNames = new HashSet<string>(StringComparer.Ordinal);

        AddQueuedListenTaskNames(checkpointPayload, haltingTaskNames.ListenTaskNames, activeTaskNames);
        AddSuspendedWaitTaskNames(checkpointPayload, haltingTaskNames.WaitTaskNames, activeTaskNames);

        return activeTaskNames.ToArray();
    }

    private static void AddQueuedListenTaskNames(
        JsonElement checkpointPayload,
        HashSet<string> listenTaskNames,
        ISet<string> activeTaskNames)
    {
        if (listenTaskNames.Count == 0
            || !checkpointPayload.TryGetProperty("runnerData", out var runnerData)
            || runnerData.ValueKind != JsonValueKind.Object
            || !runnerData.TryGetProperty("queuedMessages", out var queuedMessages)
            || queuedMessages.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in queuedMessages.EnumerateObject())
        {
            if (listenTaskNames.Contains(property.Name))
            {
                activeTaskNames.Add(property.Name);
            }
        }
    }

    private static void AddSuspendedWaitTaskNames(
        JsonElement element,
        HashSet<string> waitTaskNames,
        ISet<string> activeTaskNames)
    {
        if (waitTaskNames.Count == 0)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("__wait", out var waitState)
                    && waitState.ValueKind == JsonValueKind.Object
                    && waitState.TryGetProperty("task", out var taskNode)
                    && taskNode.ValueKind == JsonValueKind.String)
                {
                    var taskName = taskNode.GetString();
                    if (!string.IsNullOrWhiteSpace(taskName) && waitTaskNames.Contains(taskName))
                    {
                        activeTaskNames.Add(taskName);
                    }
                }

                foreach (var property in element.EnumerateObject())
                {
                    AddSuspendedWaitTaskNames(property.Value, waitTaskNames, activeTaskNames);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AddSuspendedWaitTaskNames(item, waitTaskNames, activeTaskNames);
                }

                break;
        }
    }

    private sealed record HaltingTaskNames(HashSet<string> ListenTaskNames, HashSet<string> WaitTaskNames);
    private sealed record CheckpointAnalysis(CheckpointInfo Checkpoint, int Depth, IReadOnlyCollection<string> ActiveHaltingTaskNames);

    private static IEnumerable<KeyValuePair<string, TaskDefinition>> EnumerateTasks(IDictionary<string, TaskDefinition> tasks)
    {
        foreach (var task in tasks)
        {
            yield return task;

            switch (task.Value)
            {
                case DoTaskDefinition doTask:
                    foreach (var nested in EnumerateTasks(doTask.Do))
                    {
                        yield return nested;
                    }

                    break;
                case ForkTaskDefinition forkTask:
                    foreach (var nested in forkTask.Branches.SelectMany(branch => EnumerateTasks(branch.Do)))
                    {
                        yield return nested;
                    }

                    break;
                case TryTaskDefinition tryTask:
                    foreach (var nested in EnumerateTasks(tryTask.Try))
                    {
                        yield return nested;
                    }

                    foreach (var nested in tryTask.Catch.SelectMany(c => EnumerateTasks(c.Do)))
                    {
                        yield return nested;
                    }

                    if (tryTask.Finally is not null)
                    {
                        foreach (var nested in EnumerateTasks(tryTask.Finally))
                        {
                            yield return nested;
                        }
                    }

                    break;
                case RunTaskDefinition runTask when runTask.Inline is not null:
                    foreach (var nested in EnumerateTasks(runTask.Inline.Do))
                    {
                        yield return nested;
                    }

                    break;
            }
        }
    }
}
