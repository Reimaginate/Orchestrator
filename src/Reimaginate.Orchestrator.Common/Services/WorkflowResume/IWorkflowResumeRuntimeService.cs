using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Reimaginate.Orchestrator.Abstractions;
using WorkflowEvent = Reimaginate.Orchestrator.Common.Models.WorkflowEvent;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowResume;

public interface IWorkflowResumeRuntimeService
{
    bool TryBuildResumeWakeMessage(WorkflowEvent workflowEvent, out JsonObject message);
    bool TryBuildResumeBootstrapMessage(JsonElement checkpointPayload, out JsonObject message);
    bool TryResolveInitiatingEvent(JsonObject? input, out WorkflowEvent? initiatingEvent, out bool hasInitiatingEventInput);
    (string? EventId, string? EventType, string? EventSource) ResolveInitiatingEventContext(JsonObject? input);
    bool HasQueuedRuntimeMessages(JsonElement checkpointPayload);
    Task<CheckpointInfo?> ResolveCheckpointToResumeAsync(JsonCheckpointStore store, WorkflowInstance workflowInstance);
}
