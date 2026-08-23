using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.Common.Models;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowEventProcessing;

public interface IWorkflowEventProcessingService
{
    string ActionResume { get; }
    string ActionStart { get; }
    string ActionObserved { get; }

    string BuildEventWorkflowProcessingKey(ProcessEventEnvelope envelope, string workflowType);

    IReadOnlyCollection<string> BuildDeduplicationKeys(ProcessEventEnvelope envelope, string workflowInstanceId);

    bool HasAlreadyProcessedEvent(WorkflowInstance workflowInstance, IReadOnlyCollection<string> deduplicationKeys, string currentWorkflowStatus);

    bool IsEventCurrentForCorrelation(WorkflowCorrelationLink? correlationLink, ProcessEventEnvelope envelope);

    void MarkProcessed(WorkflowInstance workflowInstance, WorkflowCorrelationLink? correlationLink, ProcessEventEnvelope envelope, IReadOnlyCollection<string> deduplicationKeys,
        DateTimeOffset utcNow, string action, string? workflowStatusAfterAction);
}
