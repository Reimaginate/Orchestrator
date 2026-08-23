namespace Reimaginate.Orchestrator.Common.Services.WorkflowExecution;

public interface IWorkflowExecutionPolicyProvider
{
    WorkflowExecutionPolicy Resolve(string? workflowType);
}
