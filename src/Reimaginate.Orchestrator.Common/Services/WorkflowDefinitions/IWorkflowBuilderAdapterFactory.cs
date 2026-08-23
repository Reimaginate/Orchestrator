using Microsoft.Agents.AI.Workflows;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowDefinitions;

public interface IWorkflowBuilderAdapterFactory
{
    IWorkflowBuilderAdapter Create(ExecutorBinding start);
}
