namespace Reimaginate.Orchestrator.Abstractions;

public interface IWorkflowInstanceLockProvider
{
    Task<WorkflowInstanceLockAcquireResult> TryAcquireAsync(string workflowInstanceId, CancellationToken cancellationToken);
}
