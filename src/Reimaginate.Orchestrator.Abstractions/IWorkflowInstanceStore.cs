namespace Reimaginate.Orchestrator.Abstractions;

public interface IWorkflowInstanceStore
{
    Task<WorkflowInstanceGetResult> GetAsync(string workflowInstanceId, CancellationToken cancellationToken);
    Task<WorkflowInstanceWriteResult> CreateAsync(WorkflowInstance workflowInstance, CancellationToken cancellationToken);
    Task<WorkflowInstanceWriteResult> ReplaceAsync(WorkflowInstance workflowInstance, string expectedConcurrencyToken, CancellationToken cancellationToken);
    Task<WorkflowInstanceUpsertResult> UpsertAsync(WorkflowInstance workflowInstance, CancellationToken cancellationToken);
    Task<WorkflowInstanceFindResult> FindByCorrelationAsync(IReadOnlyCollection<WorkflowResolvedCorrelationKey> correlationKeys, IReadOnlyCollection<string> statuses, CancellationToken cancellationToken);
    Task<WorkflowInstanceFindResult> FindByOriginatingEventIdAsync(string originatingEventId, string workflowType, IReadOnlyCollection<string> statuses, CancellationToken cancellationToken);
}
