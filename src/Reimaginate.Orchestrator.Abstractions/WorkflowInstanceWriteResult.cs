namespace Reimaginate.Orchestrator.Abstractions;

public sealed class WorkflowInstanceWriteResult
{
    public bool Success { get; set; }
    public bool Conflict { get; set; }
    public bool NotFound { get; set; }
    public string? FailureReason { get; set; }
    public string? ConcurrencyToken { get; set; }
}
