using Microsoft.Agents.AI.Workflows;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowDefinitions;

public interface IConditionalEdgeBuilder
{
    void AddUnconditionalEdge(ExecutorBinding from, ExecutorBinding to);

    void AddRouteEdge(ExecutorBinding from, ExecutorBinding to, string routeTaskName);
}
