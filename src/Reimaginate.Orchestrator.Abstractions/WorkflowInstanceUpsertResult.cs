namespace Reimaginate.Orchestrator.Abstractions;

public sealed class WorkflowInstanceUpsertResult
{
    public bool Success { get; set; }
    public string? FailureReason { get; set; }
    public string? ConcurrencyToken { get; set; }
}
