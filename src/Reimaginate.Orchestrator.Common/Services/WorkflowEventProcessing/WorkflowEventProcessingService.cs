using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.Common.Constants;
using Reimaginate.Orchestrator.Common.Models;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowEventProcessing;

public class WorkflowEventProcessingService(IWorkflowEventDeduplicationKeyStrategy deduplicationKeyStrategy) : IWorkflowEventProcessingService
{
    #region Action constants and accessors
    // Shared action names used when recording how an event affected a workflow instance.

    public const string ResumeAction = "Resume";
    public const string StartAction = "Start";
    public const string ObservedAction = "Observed";

    public string ActionResume => ResumeAction;
    public string ActionStart => StartAction;
    public string ActionObserved => ObservedAction;

    #endregion

    #region Deduplication key construction
    // Delegate key generation to the configured strategy so key format is centrally managed.

    public string BuildEventWorkflowProcessingKey(ProcessEventEnvelope envelope, string workflowType) => deduplicationKeyStrategy.BuildEventWorkflowProcessingKey(envelope, workflowType);
    public IReadOnlyCollection<string> BuildDeduplicationKeys(ProcessEventEnvelope envelope, string workflowInstanceId) => deduplicationKeyStrategy.BuildDeduplicationKeys(envelope, workflowInstanceId);

    #endregion

    #region Event processing guards
    // Prevent replay when the same event was already used to successfully complete a terminal workflow state.

    public bool HasAlreadyProcessedEvent(WorkflowInstance workflowInstance, IReadOnlyCollection<string> deduplicationKeys, string currentWorkflowStatus)
    {
        if (workflowInstance.ProcessedEvents.Count == 0 || deduplicationKeys.Count == 0)
        {
            return false;
        }

        return workflowInstance.ProcessedEvents.Any(record =>
            deduplicationKeys.Contains(record.Key, StringComparer.OrdinalIgnoreCase)
            && IsSuccessfulTerminalHandling(record, currentWorkflowStatus));
    }

    // Ignore stale events for a correlation when a newer entity version/timestamp has already been processed.
    public bool IsEventCurrentForCorrelation(WorkflowCorrelationLink? correlationLink, ProcessEventEnvelope envelope)
    {
        if (correlationLink is null)
        {
            return true;
        }

        if (envelope.EntityVersion.HasValue
            && correlationLink.LastProcessedEventVersion.HasValue
            && envelope.EntityVersion.Value < correlationLink.LastProcessedEventVersion.Value)
        {
            return false;
        }

        if (envelope.EventTimestamp.HasValue && correlationLink.LastProcessedEventTimestamp.HasValue && envelope.EventTimestamp.Value < correlationLink.LastProcessedEventTimestamp.Value)
        {
            return false;
        }

        return true;
    }

    #endregion

    #region Event recording and correlation state updates
    // Record deduplicated processing evidence and advance correlation progress markers.

    public void MarkProcessed(WorkflowInstance workflowInstance, WorkflowCorrelationLink? correlationLink, ProcessEventEnvelope envelope, IReadOnlyCollection<string> deduplicationKeys,
        DateTimeOffset utcNow, string action, string? workflowStatusAfterAction)
    {
        foreach (var deduplicationKey in deduplicationKeys)
        {
            if (workflowInstance.ProcessedEvents.Any(x => string.Equals(x.Key, deduplicationKey, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            workflowInstance.ProcessedEvents.Add(new WorkflowProcessedEventRecord
            {
                Key = deduplicationKey,
                Action = action,
                WorkflowStatusAfterAction = workflowStatusAfterAction,
                EventId = envelope.EventId,
                EventSource = envelope.EventSource,
                EventSequence = envelope.EventSequence,
                EntityId = envelope.EntityId,
                ProcessedOn = utcNow
            });
        }

        if (correlationLink is null)
        {
            return;
        }

        correlationLink.LastUpdated = utcNow;

        if (envelope.EntityVersion.HasValue)
        {
            correlationLink.LastProcessedEventVersion = envelope.EntityVersion.Value;
        }

        if (envelope.EventTimestamp.HasValue)
        {
            correlationLink.LastProcessedEventTimestamp = envelope.EventTimestamp.Value;
        }
    }

    #endregion

    #region Internal helpers
    // Only treat previous processing as a replay guard when it resulted in a terminal workflow outcome.

    private bool IsSuccessfulTerminalHandling(WorkflowProcessedEventRecord record, string currentWorkflowStatus)
    {
        if (!string.Equals(record.WorkflowStatusAfterAction, WorkflowInstanceStatuses.Completed, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(record.WorkflowStatusAfterAction, WorkflowInstanceStatuses.Cancelled, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(record.Action, ResumeAction, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(record.Action, StartAction, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(currentWorkflowStatus, record.WorkflowStatusAfterAction, StringComparison.OrdinalIgnoreCase);
    }

    #endregion
}
