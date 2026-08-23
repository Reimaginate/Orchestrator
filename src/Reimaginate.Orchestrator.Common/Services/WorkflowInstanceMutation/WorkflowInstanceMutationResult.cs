using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowInstanceMutation;

public sealed class WorkflowInstanceMutationResult
{
    public bool Success { get; set; }
    public bool Conflict { get; set; }
    public bool NotFound { get; set; }
    public string? FailureReason { get; set; }
    public WorkflowInstance? WorkflowInstance { get; set; }
}
