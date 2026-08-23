namespace Reimaginate.Orchestrator.Abstractions;

public class WorkflowExecutionFailureDetails
{
    public string? TaskName { get; set; }
    public string? LocationHint { get; set; }
    public string? ExceptionType { get; set; }
    public List<string> Messages { get; set; } = [];
}
