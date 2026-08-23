using Reimaginate.Orchestrator.Common.Models;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowEventProcessing;

public interface IWorkflowEventDeduplicationKeyStrategy
{
    string BuildEventWorkflowProcessingKey(ProcessEventEnvelope envelope, string workflowType);

    IReadOnlyCollection<string> BuildDeduplicationKeys(ProcessEventEnvelope envelope, string workflowInstanceId);
}
