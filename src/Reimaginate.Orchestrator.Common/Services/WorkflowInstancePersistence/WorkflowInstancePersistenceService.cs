using Reimaginate.Mediator;
using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.Common.Requests.Internal.PersistWorkflowInstance;
using Reimaginate.Orchestrator.Common.Services.WorkflowExecution;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowInstancePersistence;

public class WorkflowInstancePersistenceService(IMediator mediator) : IWorkflowInstancePersistenceService
{
    public async Task PersistWorkflowInstanceAsync(
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
        WorkflowExecutionPolicyOverride? executionPolicyOverride = null)
    {
        var (response, error) = await mediator.TrySend(new PersistWorkflowInstanceRequest
        {
            WorkflowInstanceId = workflowInstanceId,
            WorkflowType = workflowType,
            Status = status,
            FailureReason = failureReason,
            FailureDetails = failureDetails,
            FailedOn = failedOn,
            CurrentCheckpointId = currentCheckpointId,
            CheckpointRunId = checkpointRunId,
            AwaitingEvents = awaitingEvents,
            OriginatingEventId = originatingEventId,
            OriginatingEventType = originatingEventType,
            OriginatingEventSource = originatingEventSource,
            Metadata = metadata,
            ExecutionPolicyOverride = executionPolicyOverride
        }, cancellationToken);

        if (response is null || error is not null || !response.Success)
        {
            throw new InvalidOperationException(response?.FailureReason ?? "Failed to persist workflow instance.");
        }
    }
}
