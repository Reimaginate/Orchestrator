namespace Reimaginate.Orchestrator.Abstractions;

public sealed class WorkflowInstanceGetResult
{
    public bool Success { get; set; }
    public bool NotFound { get; set; }
    public string? FailureReason { get; set; }
    public WorkflowInstance? WorkflowInstance { get; set; }
}
