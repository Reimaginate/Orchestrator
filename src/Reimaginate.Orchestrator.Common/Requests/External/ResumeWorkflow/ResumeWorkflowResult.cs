using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.Common.Requests.External.ResumeWorkflow;

public class ResumeWorkflowResult : Result
{
    public string? WorkflowInstanceId { get; set; }
    public string? DiagnosticsPath { get; set; }
    public string? Status { get; set; }
    public WorkflowExecutionFailureDetails? FailureDetails { get; set; }
    public object? FinalOutput { get; set; }
}
