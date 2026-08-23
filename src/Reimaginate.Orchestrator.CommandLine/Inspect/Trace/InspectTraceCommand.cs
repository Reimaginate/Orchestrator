using System.CommandLine;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Reimaginate.CLI.Base.Abstractions;
using Reimaginate.CLI.Base.Attributes;
using Reimaginate.Orchestrator.Common.Diagnostics;

namespace Reimaginate.Orchestrator.CommandLine.Inspect.Trace;

[Argument("workflowInstanceId", typeof(string), required: true, allowMultiple: false, description: "Workflow instance id")]
[Option("json", typeof(bool), required: false, description: "Write raw JSON output")]
public class InspectTraceCommand : SubCommand<InspectCommand>
{
    private readonly IServiceProvider _serviceProvider;

    public InspectTraceCommand(IServiceProvider serviceProvider) : base("trace", serviceProvider)
    {
        _serviceProvider = serviceProvider;

        SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var workflowInstanceId = parseResult.GetValue<string>("workflowInstanceId");
            var json = parseResult.GetValue<bool>("--json");
            return HandleCommand(workflowInstanceId!, json, cancellationToken);
        });
    }

    private Task<int> HandleCommand(string workflowInstanceId, bool asJson, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pathResolver = _serviceProvider.GetRequiredService<IWorkflowDiagnosticsPathResolver>();
        var diagnosticsPath = pathResolver.TryGetWorkflowSessionPath(workflowInstanceId);
        var timelinePath = string.IsNullOrWhiteSpace(diagnosticsPath) ? null : Path.Combine(diagnosticsPath, "timeline.jsonl");
        if (string.IsNullOrWhiteSpace(timelinePath) || !File.Exists(timelinePath))
        {
            Console.WriteLine($"No trace file was found for workflow instance '{workflowInstanceId}'.");
            return Task.FromResult(1);
        }

        var timeline = DiagnosticsCliOutput.ReadJsonLines(timelinePath);
        if (asJson)
        {
            DiagnosticsCliOutput.WriteJson(new JsonArray(timeline.Select(entry => entry.DeepClone()).ToArray()));
            return Task.FromResult(0);
        }

        foreach (var entry in timeline)
        {
            var timestamp = entry["timestampUtc"]?.GetValue<string>() ?? "<unknown>";
            var eventType = entry["eventType"]?.GetValue<string>() ?? "<unknown>";
            var payload = entry["payload"] as JsonObject;
            var taskName = payload?["taskName"]?.GetValue<string>();
            var routeTaskName = payload?["routeTaskName"]?.GetValue<string>() ?? payload?["targetTaskName"]?.GetValue<string>();
            var status = payload?["status"]?.GetValue<string>();
            var checkpointId = payload?["checkpointId"]?.GetValue<string>();
            var message = payload?["message"]?.GetValue<string>() ?? payload?["reason"]?.GetValue<string>();

            var fragments = new List<string>();
            if (!string.IsNullOrWhiteSpace(taskName)) fragments.Add($"task={taskName}");
            if (!string.IsNullOrWhiteSpace(routeTaskName)) fragments.Add($"route={routeTaskName}");
            if (!string.IsNullOrWhiteSpace(status)) fragments.Add($"status={status}");
            if (!string.IsNullOrWhiteSpace(checkpointId)) fragments.Add($"checkpoint={checkpointId}");
            if (!string.IsNullOrWhiteSpace(message)) fragments.Add($"detail={message}");

            Console.WriteLine($"[{timestamp}] {eventType}{(fragments.Count == 0 ? string.Empty : $" | {string.Join(", ", fragments)}")}");
        }

        return Task.FromResult(0);
    }
}
