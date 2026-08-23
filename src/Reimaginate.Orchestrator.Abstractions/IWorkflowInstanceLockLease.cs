namespace Reimaginate.Orchestrator.Abstractions;

public interface IWorkflowInstanceLockLease : IAsyncDisposable
{
    string WorkflowInstanceId { get; }
    CancellationToken LostToken { get; }
}
