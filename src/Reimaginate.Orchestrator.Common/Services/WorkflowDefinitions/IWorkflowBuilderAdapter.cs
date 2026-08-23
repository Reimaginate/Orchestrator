using Microsoft.Agents.AI.Workflows;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowDefinitions;

public interface IWorkflowBuilderAdapter
{
    bool SupportsRouteEdges { get; }

    IConditionalEdgeBuilder EdgeBuilder { get; }

    void WithOutputFrom(params ExecutorBinding[] executors);

    Workflow Build();
}
