using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Reimaginate.CLI.Base.Abstractions;
using Reimaginate.CLI.Base.Attributes;
using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.Common.Diagnostics;

namespace Reimaginate.Orchestrator.CommandLine.Inspect.Workflow;

[Argument("workflowInstanceId", typeof(string), required: true, allowMultiple: false, description: "Workflow instance id")]
[Option("json", typeof(bool), required: false, description: "Write raw JSON output")]
public class InspectWorkflowCommand : SubCommand<InspectCommand>
{
    private readonly IServiceProvider _serviceProvider;

    public InspectWorkflowCommand(IServiceProvider serviceProvider) : base("workflow", serviceProvider)
    {
        _serviceProvider = serviceProvider;

        SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var workflowInstanceId = parseResult.GetValue<string>("workflowInstanceId");
            var json = parseResult.GetValue<bool>("--json");
            return await HandleCommand(workflowInstanceId!, json, cancellationToken);
        });
    }

    private async Task<int> HandleCommand(string workflowInstanceId, bool asJson, CancellationToken cancellationToken)
    {
        var workflowInstanceStore = _serviceProvider.GetRequiredService<IWorkflowInstanceStore>();
        var diagnosticsPathResolver = _serviceProvider.GetRequiredService<IWorkflowDiagnosticsPathResolver>();

        var result = await workflowInstanceStore.GetAsync(workflowInstanceId, cancellationToken);
        if (!result.Success || result.NotFound || result.WorkflowInstance is null)
        {
            Console.WriteLine(result.FailureReason ?? $"Workflow instance '{workflowInstanceId}' was not found.");
            return 1;
        }

        var workflowInstance = result.WorkflowInstance;
        var diagnosticsPath = diagnosticsPathResolver.TryGetWorkflowSessionPath(workflowInstanceId);
        var timelinePath = string.IsNullOrWhiteSpace(diagnosticsPath) ? null : Path.Combine(diagnosticsPath, "timeline.jsonl");
        var timeline = string.IsNullOrWhiteSpace(timelinePath) ? [] : DiagnosticsCliOutput.ReadJsonLines(timelinePath);
        var latestRoute = DiagnosticsCliOutput.FindLatest(timeline,
            "executor.switch.route_selected",
            "executor.loop.route_selected",
            "executor.foreach.route_selected",
            "executor.fork.route_selected");
        var latestFailure = DiagnosticsCliOutput.FindLatest(timeline,
            "workflow.error",
            "executor.loop.guard_failed",
            "executor.emit.error_captured",
            "executor.call.error_captured");

        var document = new JsonObject
        {
            ["workflowInstanceId"] = workflowInstance.Id,
            ["workflowType"] = workflowInstance.WorkflowType,
            ["status"] = workflowInstance.Status,
            ["failureReason"] = workflowInstance.FailureReason,
            ["failureDetails"] = JsonSerializer.SerializeToNode(workflowInstance.FailureDetails),
            ["failedOn"] = workflowInstance.FailedOn,
            ["currentCheckpointId"] = workflowInstance.CurrentCheckpointId,
            ["checkpointRunId"] = workflowInstance.WorkflowInstanceId,
            ["diagnosticsPath"] = diagnosticsPath,
            ["awaitingEvents"] = JsonSerializer.SerializeToNode(workflowInstance.AwaitingEvents) as JsonArray ?? new JsonArray(),
            ["latestRoute"] = latestRoute,
            ["latestFailure"] = latestFailure
        };

        if (asJson)
        {
            DiagnosticsCliOutput.WriteJson(document);
            return 0;
        }

        Console.WriteLine($"WorkflowInstanceId: {workflowInstance.Id}");
        Console.WriteLine($"WorkflowType: {workflowInstance.WorkflowType}");
        Console.WriteLine($"Status: {workflowInstance.Status}");
        Console.WriteLine($"FailedOn: {workflowInstance.FailedOn?.ToString("O") ?? "<none>"}");
        Console.WriteLine($"FailureReason: {workflowInstance.FailureReason ?? "<none>"}");
        Console.WriteLine($"FailureTask: {workflowInstance.FailureDetails?.TaskName ?? "<none>"}");
        Console.WriteLine($"FailureLocation: {workflowInstance.FailureDetails?.LocationHint ?? "<none>"}");
        Console.WriteLine($"CurrentCheckpointId: {workflowInstance.CurrentCheckpointId ?? "<none>"}");
        Console.WriteLine($"CheckpointRunId: {workflowInstance.WorkflowInstanceId ?? "<none>"}");
        Console.WriteLine($"DiagnosticsPath: {diagnosticsPath ?? "<disabled>"}");
        Console.WriteLine($"AwaitingEvents: {(workflowInstance.AwaitingEvents.Count == 0 ? "<none>" : string.Join(", ", workflowInstance.AwaitingEvents.Select(x => x.TaskName)))}");

        if (latestRoute is not null)
        {
            Console.WriteLine($"LatestRoute: {latestRoute["eventType"]?.GetValue<string>()} -> {latestRoute["payload"]?["routeTaskName"]?.GetValue<string>() ?? latestRoute["payload"]?["targetTaskName"]?.GetValue<string>()}");
        }

        if (latestFailure is not null)
        {
            Console.WriteLine($"LatestFailure: {latestFailure["payload"]?["message"]?.GetValue<string>() ?? latestFailure["payload"]?["reason"]?.GetValue<string>()}");
        }

        return 0;
    }
}
