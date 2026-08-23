namespace Reimaginate.Orchestrator.Abstractions;

public sealed class WorkflowInstanceLockAcquireResult
{
    public bool Success { get; set; }
    public bool LockAcquired { get; set; }
    public string? FailureReason { get; set; }
    public IWorkflowInstanceLockLease? Lease { get; set; }
}
