namespace Reimaginate.Orchestrator.Common.Diagnostics;

public interface IWorkflowDiagnosticsPathResolver
{
    bool IsEnabled { get; }
    string? TryGetRootPath();
    string? TryGetWorkflowSessionPath(string workflowInstanceId);
    string? TryGetWorkflowTypeGraphPath(string workflowType);
}
