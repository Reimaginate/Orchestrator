using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.Common.Requests.External.StartWorkflow;

public class StartWorkflowResult : Result
{
    public string? WorkflowInstanceId { get; set; } = null!;
    public string? DiagnosticsPath { get; set; }
    public string? Status { get; set; }
    public WorkflowExecutionFailureDetails? FailureDetails { get; set; }
    public object? FinalOutput { get; set; }
}
