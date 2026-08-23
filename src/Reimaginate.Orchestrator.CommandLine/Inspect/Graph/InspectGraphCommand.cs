using System.CommandLine;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Reimaginate.CLI.Base.Abstractions;
using Reimaginate.CLI.Base.Attributes;
using Reimaginate.Mediator;
using Reimaginate.Orchestrator.Common.Diagnostics;
using Reimaginate.Orchestrator.Common.Requests.Internal.BuildAgentWorkflow;
using Reimaginate.Orchestrator.Common.Requests.Internal.ResolveAndLoadWorkflowDefinition;

namespace Reimaginate.Orchestrator.CommandLine.Inspect.Graph;

[Argument("workflowType", typeof(string), required: true, allowMultiple: false, description: "Workflow type")]
[Option("json", typeof(bool), required: false, description: "Write raw JSON output")]
public class InspectGraphCommand : SubCommand<InspectCommand>
{
    private readonly IServiceProvider _serviceProvider;

    public InspectGraphCommand(IServiceProvider serviceProvider) : base("graph", serviceProvider)
    {
        _serviceProvider = serviceProvider;

        SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var workflowType = parseResult.GetValue<string>("workflowType");
            var json = parseResult.GetValue<bool>("--json");
            return await HandleCommand(workflowType!, json, cancellationToken);
        });
    }

    private async Task<int> HandleCommand(string workflowType, bool asJson, CancellationToken cancellationToken)
    {
        var pathResolver = _serviceProvider.GetRequiredService<IWorkflowDiagnosticsPathResolver>();
        var graphPath = pathResolver.TryGetWorkflowTypeGraphPath(workflowType);
        JsonObject? graph = !string.IsNullOrWhiteSpace(graphPath) ? DiagnosticsCliOutput.ReadJsonObject(graphPath) : null;

        if (graph is null)
        {
            var mediator = _serviceProvider.GetRequiredService<IMediator>();
            var traceSink = _serviceProvider.GetRequiredService<IWorkflowExecutionTraceSink>();
            var (definitionResponse, definitionError) = await mediator.TrySend(new ResolveAndLoadWorkflowDefinitionRequest
            {
                WorkflowType = workflowType
            }, cancellationToken);

            if (definitionResponse is null || definitionError is not null || !definitionResponse.Success)
            {
                Console.WriteLine(definitionResponse?.FailureReason ?? definitionError?.Message ?? $"Workflow definition '{workflowType}' could not be resolved.");
                return 1;
            }

            var (workflowResponse, workflowError) = await mediator.TrySend(new BuildAgentWorkflowRequest
            {
                WorkflowType = workflowType,
                Ast = definitionResponse.Ast,
                Definition = definitionResponse.Definition
            }, cancellationToken);

            if (workflowResponse is null || workflowError is not null || !workflowResponse.Success)
            {
                Console.WriteLine(workflowResponse?.FailureReason ?? workflowError?.Message ?? $"Workflow '{workflowType}' could not be built.");
                return 1;
            }

            graph = workflowResponse.DiagnosticsGraph;
            graph["workflowType"] = workflowType;
            if (graph.Count > 0)
            {
                await traceSink.WriteGraphAsync(workflowType, graph, null, cancellationToken);
            }
        }

        if (asJson)
        {
            DiagnosticsCliOutput.WriteJson(graph);
            return 0;
        }

        var startTaskName = graph["startTaskName"]?.GetValue<string>() ?? "<unknown>";
        var nodeCount = (graph["nodes"] as JsonArray)?.Count ?? 0;
        var transitionCount = (graph["transitions"] as JsonArray)?.Count ?? 0;

        Console.WriteLine($"WorkflowType: {workflowType}");
        Console.WriteLine($"StartTaskName: {startTaskName}");
        Console.WriteLine($"NodeCount: {nodeCount}");
        Console.WriteLine($"TransitionCount: {transitionCount}");

        if (graph["branches"] is JsonArray branches && branches.Count > 0)
        {
            Console.WriteLine("Routes:");
            foreach (var branchGroup in branches.OfType<JsonObject>())
            {
                var taskName = branchGroup["taskName"]?.GetValue<string>() ?? "<unknown>";
                if (branchGroup["branches"] is not JsonArray branchEntries)
                {
                    continue;
                }

                foreach (var branch in branchEntries.OfType<JsonObject>())
                {
                    Console.WriteLine($"  {taskName} -> {branch["targetTaskName"]?.GetValue<string>()} ({branch["when"]?.GetValue<string>() ?? "default"})");
                }
            }
        }

        return 0;
    }
}
