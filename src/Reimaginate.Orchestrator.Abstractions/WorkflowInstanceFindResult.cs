namespace Reimaginate.Orchestrator.Abstractions;

public sealed class WorkflowInstanceFindResult
{
    public bool Success { get; set; }
    public string? FailureReason { get; set; }
    public List<WorkflowInstance> WorkflowInstances { get; set; } = [];
}
