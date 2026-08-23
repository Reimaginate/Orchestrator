using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Reimaginate.Orchestrator.Abstractions;
using WorkflowEvent = Reimaginate.Orchestrator.Common.Models.WorkflowEvent;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowResume;

public sealed class WorkflowResumeRuntimeService : IWorkflowResumeRuntimeService
{
    private const string WaitStateProperty = "__wait";

    public bool TryBuildResumeWakeMessage(WorkflowEvent workflowEvent, out JsonObject message)
    {
        var resumeNode = JsonSerializer.SerializeToNode(workflowEvent) as JsonObject;
        if (resumeNode is null)
        {
            message = null!;
            return false;
        }

        message = new JsonObject
        {
            ["event"] = (JsonObject)resumeNode.DeepClone(),
            ["resumeEvent"] = (JsonObject)resumeNode.DeepClone()
        };

        return true;
    }

    public bool TryBuildResumeBootstrapMessage(JsonElement checkpointPayload, out JsonObject message)
    {
        try
        {
            if (TryFindSuspendedWaitPayload(checkpointPayload, out var payload))
            {
                message = payload;
                return true;
            }
        }
        catch
        {
            // Fall back to the empty bootstrap payload when checkpoint shapes change unexpectedly.
        }

        message = null!;
        return false;
    }

    public bool TryResolveInitiatingEvent(JsonObject? input, out WorkflowEvent? initiatingEvent, out bool hasInitiatingEventInput)
    {
        initiatingEvent = null;
        hasInitiatingEventInput = false;

        if (input is null)
        {
            return false;
        }

        if (!input.TryGetPropertyValue("__initiatingEvent", out var initiatingEventNode))
        {
            return false;
        }

        hasInitiatingEventInput = true;

        if (initiatingEventNode is null)
        {
            return false;
        }

        try
        {
            var parsedEvent = initiatingEventNode.Deserialize<WorkflowEvent>();
            if (parsedEvent is null)
            {
                return false;
            }

            initiatingEvent = parsedEvent;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }


    public (string? EventId, string? EventType, string? EventSource) ResolveInitiatingEventContext(JsonObject? input)
    {
        if (!TryResolveInitiatingEvent(input, out var initiatingEvent, out _)
            || initiatingEvent is null)
        {
            return (null, null, null);
        }

        return (initiatingEvent.EventId, initiatingEvent.EventType, initiatingEvent.EventSource);
    }

    public bool HasQueuedRuntimeMessages(JsonElement checkpointPayload)
    {
        try
        {
            return HasQueuedRuntimeMessages(checkpointPayload, depth: 0);
        }
        catch
        {
            // Fail safe on unexpected payload shapes.
            return true;
        }
    }

    public async Task<CheckpointInfo?> ResolveCheckpointToResumeAsync(JsonCheckpointStore store, WorkflowInstance workflowInstance)
    {
        foreach (var candidateRunId in ResolveCandidateRunIds(workflowInstance))
        {
            var checkpoints = (await store.RetrieveIndexAsync(candidateRunId)).ToArray();
            if (checkpoints.Length == 0)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(workflowInstance.CurrentCheckpointId))
            {
                var exactCheckpoint = checkpoints.FirstOrDefault(checkpoint =>
                    string.Equals(checkpoint.CheckpointId, workflowInstance.CurrentCheckpointId, StringComparison.Ordinal));
                if (exactCheckpoint is not null)
                {
                    return exactCheckpoint;
                }
            }

            var resumableCheckpoint = await ResolveMostResumableCheckpointAsync(store, checkpoints);
            if (resumableCheckpoint is not null)
            {
                return resumableCheckpoint;
            }

            var latestCheckpoint = checkpoints
                .OrderByDescending(c => c.CheckpointId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (latestCheckpoint is not null)
            {
                return latestCheckpoint;
            }
        }

        return null;
    }

    private async Task<CheckpointInfo?> ResolveMostResumableCheckpointAsync(JsonCheckpointStore store, IReadOnlyList<CheckpointInfo> checkpoints)
    {
        CheckpointInfo? selectedCheckpoint = null;
        var selectedScore = 0;

        foreach (var checkpoint in checkpoints)
        {
            var score = await ScoreCheckpointForResumeAsync(store, checkpoint);
            if (score <= 0)
            {
                continue;
            }

            if (selectedCheckpoint is null
                || score > selectedScore
                || (score == selectedScore
                    && string.CompareOrdinal(checkpoint.CheckpointId, selectedCheckpoint.CheckpointId) > 0))
            {
                selectedCheckpoint = checkpoint;
                selectedScore = score;
            }
        }

        return selectedCheckpoint;
    }

    private async Task<int> ScoreCheckpointForResumeAsync(JsonCheckpointStore store, CheckpointInfo checkpoint)
    {
        try
        {
            var payload = await store.RetrieveCheckpointAsync(checkpoint.SessionId, checkpoint);
            var hasChildren = (await store.RetrieveIndexAsync(checkpoint.SessionId, checkpoint)).Any();

            if (TryFindSuspendedWaitPayload(payload, out _))
            {
                return hasChildren ? 2 : 3;
            }

            if (HasQueuedRuntimeMessages(payload))
            {
                return hasChildren ? 1 : 2;
            }
        }
        catch
        {
            // Ignore unreadable checkpoints and fall back to the next candidate.
        }

        return 0;
    }

    private static IEnumerable<string> ResolveCandidateRunIds(WorkflowInstance workflowInstance)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(workflowInstance.WorkflowInstanceId)
            && seen.Add(workflowInstance.WorkflowInstanceId))
        {
            yield return workflowInstance.WorkflowInstanceId;
        }

        if (!string.IsNullOrWhiteSpace(workflowInstance.Id)
            && seen.Add(workflowInstance.Id))
        {
            yield return workflowInstance.Id;
        }
    }

    private static bool HasQueuedRuntimeMessages(JsonElement element, int depth)
    {
        if (depth > 32)
        {
            return true;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (LooksLikeQueuedMessageField(property.Name))
                    {
                        if (HasQueuedMessagesValue(property.Value, depth + 1))
                        {
                            return true;
                        }

                        continue;
                    }

                    if (HasQueuedRuntimeMessages(property.Value, depth + 1))
                    {
                        return true;
                    }
                }

                return false;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (HasQueuedRuntimeMessages(item, depth + 1))
                    {
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }

    private static bool HasQueuedMessagesValue(JsonElement value, int depth)
    {
        if (depth > 32)
        {
            return true;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                return value.GetArrayLength() > 0;

            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    if (HasQueuedMessagesValue(property.Value, depth + 1))
                    {
                        return true;
                    }
                }

                return false;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return false;

            default:
                // Unexpected scalar queue representations are treated conservatively as queued.
                return true;
        }
    }

    private static bool LooksLikeQueuedMessageField(string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        var normalized = propertyName.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        return normalized.Contains("queuedmessages", StringComparison.Ordinal)
               || normalized.Contains("queuedmessage", StringComparison.Ordinal)
               || normalized.Contains("messageenvelope", StringComparison.Ordinal)
               || normalized.Contains("messageenvelopes", StringComparison.Ordinal)
               || normalized.Contains("runtimemessages", StringComparison.Ordinal)
               || normalized.Contains("queuedruntime", StringComparison.Ordinal);
    }

    private static bool TryFindSuspendedWaitPayload(JsonElement element, out JsonObject payload)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (LooksLikeSuspendedWaitPayload(element))
                {
                    payload = JsonNode.Parse(element.GetRawText()) as JsonObject ?? new JsonObject();
                    return true;
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (TryFindSuspendedWaitPayload(property.Value, out payload))
                    {
                        return true;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindSuspendedWaitPayload(item, out payload))
                    {
                        return true;
                    }
                }

                break;
        }

        payload = null!;
        return false;
    }

    private static bool LooksLikeSuspendedWaitPayload(JsonElement element)
    {
        if (!element.TryGetProperty(WaitStateProperty, out var waitState)
            || waitState.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var hasTask = waitState.TryGetProperty("task", out var task) && task.ValueKind == JsonValueKind.String;
        var hasUntilUtc = waitState.TryGetProperty("untilUtc", out var untilUtc) && untilUtc.ValueKind == JsonValueKind.String;
        return hasTask && hasUntilUtc;
    }
}
