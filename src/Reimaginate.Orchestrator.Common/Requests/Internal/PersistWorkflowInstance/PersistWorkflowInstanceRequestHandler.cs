using Reimaginate.Mediator;
using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.Common.Config;
using Reimaginate.Orchestrator.Common.Constants;
using Reimaginate.Orchestrator.Common.Diagnostics;
using Reimaginate.Orchestrator.Common.Services.WorkflowExecution;
using Reimaginate.Orchestrator.Common.Services.WorkflowInstanceMutation;

namespace Reimaginate.Orchestrator.Common.Requests.Internal.PersistWorkflowInstance;

public class PersistWorkflowInstanceRequestHandler(
    IWorkflowInstanceMutationService workflowInstanceMutationService,
    IWorkflowExecutionTraceSink workflowExecutionTraceSink,
    IWorkflowExecutionPolicyProvider workflowExecutionPolicyProvider) : IHandler<PersistWorkflowInstanceRequest, PersistWorkflowInstanceResponse>
{
    public async Task<PersistWorkflowInstanceResponse> HandleAsync(PersistWorkflowInstanceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var workflowInstancesMode = workflowExecutionPolicyProvider.Resolve(request.WorkflowType)
                .Apply(request.ExecutionPolicyOverride)
                .WorkflowInstances;
            if (workflowInstancesMode == WorkflowInstancePersistenceMode.Disabled
                || (workflowInstancesMode == WorkflowInstancePersistenceMode.Failure
                    && !string.Equals(request.Status, WorkflowInstanceStatuses.Failed, StringComparison.OrdinalIgnoreCase)))
            {
                return new PersistWorkflowInstanceResponse
                {
                    Success = true
                };
            }

            var result = await workflowInstanceMutationService.UpsertAsync(
                request.WorkflowInstanceId,
                (existing, utcNow) =>
                {
                    var mergedMetadata = existing?.Metadata is { Count: > 0 }
                        ? new Dictionary<string, string>(existing.Metadata, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    if (request.Metadata is { Count: > 0 })
                    {
                        foreach (var entry in request.Metadata)
                        {
                            mergedMetadata[entry.Key] = entry.Value;
                        }
                    }

                    var document = new WorkflowInstance
                    {
                        Id = request.WorkflowInstanceId,
                        WorkflowType = request.WorkflowType,
                        Status = request.Status,
                        FailureReason = string.Equals(request.Status, WorkflowInstanceStatuses.Failed, StringComparison.OrdinalIgnoreCase)
                            ? request.FailureReason
                            : null,
                        FailureDetails = string.Equals(request.Status, WorkflowInstanceStatuses.Failed, StringComparison.OrdinalIgnoreCase)
                            ? request.FailureDetails
                            : null,
                        FailedOn = string.Equals(request.Status, WorkflowInstanceStatuses.Failed, StringComparison.OrdinalIgnoreCase)
                            ? request.FailedOn ?? existing?.FailedOn ?? utcNow
                            : null,
                        CurrentCheckpointId = request.CurrentCheckpointId,
                        WorkflowInstanceId = request.CheckpointRunId,
                        CorrelationKeys = existing?.CorrelationKeys ?? [],
                        Correlations = request.Correlations ?? existing?.Correlations ?? [],
                        ProcessedEvents = existing?.ProcessedEvents ?? [],
                        AwaitingEvents = request.AwaitingEvents ?? existing?.AwaitingEvents ?? [],
                        Metadata = mergedMetadata,
                        ExtensionData = existing?.ExtensionData,
                        OriginatingEventId = request.OriginatingEventId ?? existing?.OriginatingEventId,
                        OriginatingEventType = request.OriginatingEventType ?? existing?.OriginatingEventType,
                        OriginatingEventSource = request.OriginatingEventSource ?? existing?.OriginatingEventSource,
                        CreatedOn = existing?.CreatedOn,
                        LastUpdated = utcNow
                    };

                    if (!string.Equals(request.Status, WorkflowInstanceStatuses.Running, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(request.Status, WorkflowInstanceStatuses.Suspended, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(request.Status, WorkflowInstanceStatuses.Starting, StringComparison.OrdinalIgnoreCase))
                    {
                        document.AwaitingEvents = [];
                    }

                    if (string.Equals(request.Status, WorkflowInstanceStatuses.Running, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(request.Status, WorkflowInstanceStatuses.Suspended, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(request.Status, WorkflowInstanceStatuses.Starting, StringComparison.OrdinalIgnoreCase))
                    {
                        document.CreatedOn ??= utcNow;
                    }

                    foreach (var correlation in document.Correlations)
                    {
                        correlation.CreatedOn ??= utcNow;
                        correlation.LastUpdated = utcNow;
                    }

                    return ValueTask.FromResult(WorkflowInstanceMutationCommand.Write(document));
                },
                cancellationToken);

            if (result.Success && result.WorkflowInstance is not null)
            {
                await workflowExecutionTraceSink.WriteWorkflowInstanceSnapshotAsync(result.WorkflowInstance, cancellationToken);
            }

            return new PersistWorkflowInstanceResponse
            {
                Success = result.Success,
                FailureReason = result.FailureReason
            };
        }
        catch (Exception ex)
        {
            return new PersistWorkflowInstanceResponse
            {
                Success = false,
                FailureReason = ex.Message
            };
        }
    }
}
