using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.Common.Services.WorkflowExecution;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowInstancePersistence;

public interface IWorkflowInstancePersistenceService
{
    Task PersistWorkflowInstanceAsync(
        string workflowInstanceId,
        string workflowType,
        string status,
        string? failureReason,
        WorkflowExecutionFailureDetails? failureDetails,
        DateTimeOffset? failedOn,
        string? currentCheckpointId,
        string? checkpointRunId,
        List<WorkflowAwaitingEventDescriptor> awaitingEvents,
        CancellationToken cancellationToken,
        string? originatingEventId = null,
        string? originatingEventType = null,
        string? originatingEventSource = null,
        Dictionary<string, string>? metadata = null,
        WorkflowExecutionPolicyOverride? executionPolicyOverride = null);
}
