using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Workflows;
using Reimaginate.Orchestrator.Common.Executors;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowDefinitions;

public sealed class MafWorkflowBuilderAdapter(ExecutorBinding start) : IWorkflowBuilderAdapter, IConditionalEdgeBuilder
{
    private readonly WorkflowBuilder _workflowBuilder = new(start);

    public bool SupportsRouteEdges => true;

    public IConditionalEdgeBuilder EdgeBuilder => this;

    public void AddUnconditionalEdge(ExecutorBinding from, ExecutorBinding to)
    {
        _workflowBuilder.AddEdge(from, to);
    }

    public void AddRouteEdge(ExecutorBinding from, ExecutorBinding to, string routeTaskName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeTaskName);
        _workflowBuilder.AddEdge(from, to, (JsonObject? payload) => payload is not null && ContainsSelectedRoute(payload, routeTaskName));
    }

    public void WithOutputFrom(params ExecutorBinding[] executors)
    {
        _workflowBuilder.WithOutputFrom(executors);
    }

    public Workflow Build()
    {
        return _workflowBuilder.Build(validateOrphans: false);
    }

    private static bool ContainsSelectedRoute(JsonObject payload, string expectedRouteName)
    {
        if (payload[SwitchTaskExecutor.SelectedRouteProperty]?.GetValue<string>() is not { Length: > 0 } routeName)
        {
            return false;
        }

        return string.Equals(routeName, expectedRouteName, StringComparison.Ordinal);
    }
}
