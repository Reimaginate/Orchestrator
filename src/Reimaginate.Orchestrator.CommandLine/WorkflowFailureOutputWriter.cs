using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.CommandLine;

internal static class WorkflowFailureOutputWriter
{
    public static void WriteIfPresent(string? workflowInstanceId, string? diagnosticsPath, WorkflowExecutionFailureDetails? failureDetails)
    {
        if (!string.IsNullOrWhiteSpace(workflowInstanceId))
        {
            Console.WriteLine($"WorkflowInstanceId: {workflowInstanceId}");
        }

        if (!string.IsNullOrWhiteSpace(diagnosticsPath))
        {
            Console.WriteLine($"DiagnosticsPath: {diagnosticsPath}");
        }

        if (!string.IsNullOrWhiteSpace(failureDetails?.TaskName))
        {
            Console.WriteLine($"FailedTask: {failureDetails.TaskName}");
        }

        if (!string.IsNullOrWhiteSpace(failureDetails?.LocationHint))
        {
            Console.WriteLine($"Location: {failureDetails.LocationHint}");
        }

        if (!string.IsNullOrWhiteSpace(failureDetails?.ExceptionType))
        {
            Console.WriteLine($"ExceptionType: {failureDetails.ExceptionType}");
        }

        if (!string.IsNullOrWhiteSpace(workflowInstanceId))
        {
            Console.WriteLine($"NextStep: inspect trace {workflowInstanceId}");
        }
    }
}
