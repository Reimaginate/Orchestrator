using System.CommandLine;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Reimaginate.CLI.Base.Abstractions;
using Reimaginate.CLI.Base.Attributes;
using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.CommandLine.Inspect.Checkpoint;

[Argument("workflowInstanceId", typeof(string), required: true, allowMultiple: false, description: "Workflow instance id")]
[Option("checkpoint-id", typeof(string), required: false, description: "Checkpoint id override")]
[Option("json", typeof(bool), required: false, description: "Write raw JSON output")]
public class InspectCheckpointCommand : SubCommand<InspectCommand>
{
    private readonly IServiceProvider _serviceProvider;

    public InspectCheckpointCommand(IServiceProvider serviceProvider) : base("checkpoint", serviceProvider)
    {
        _serviceProvider = serviceProvider;

        SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var workflowInstanceId = parseResult.GetValue<string>("workflowInstanceId");
            var checkpointId = parseResult.GetValue<string>("--checkpoint-id");
            var json = parseResult.GetValue<bool>("--json");
            return await HandleCommand(workflowInstanceId!, checkpointId, json, cancellationToken);
        });
    }

    private async Task<int> HandleCommand(string workflowInstanceId, string? checkpointIdOverride, bool asJson, CancellationToken cancellationToken)
    {
        var workflowInstanceStore = _serviceProvider.GetRequiredService<IWorkflowInstanceStore>();
        var checkpointStorage = _serviceProvider.GetRequiredService<ICheckpointStorage>();

        var instanceResult = await workflowInstanceStore.GetAsync(workflowInstanceId, cancellationToken);
        if (!instanceResult.Success || instanceResult.NotFound || instanceResult.WorkflowInstance is null)
        {
            Console.WriteLine(instanceResult.FailureReason ?? $"Workflow instance '{workflowInstanceId}' was not found.");
            return 1;
        }

        var workflowInstance = instanceResult.WorkflowInstance;
        var checkpointId = checkpointIdOverride ?? workflowInstance.CurrentCheckpointId;
        var checkpointRunId = workflowInstance.WorkflowInstanceId ?? workflowInstance.Id;
        if (string.IsNullOrWhiteSpace(checkpointId))
        {
            Console.WriteLine($"Workflow instance '{workflowInstanceId}' does not have a current checkpoint.");
            return 1;
        }

        var store = checkpointStorage.CreateStore();
        var payload = await store.RetrieveCheckpointAsync(checkpointRunId, new CheckpointInfo(checkpointRunId, checkpointId));
        var payloadNode = JsonNode.Parse(payload.GetRawText());

        if (asJson)
        {
            DiagnosticsCliOutput.WriteJson(payloadNode);
            return 0;
        }

        var payloadObject = payloadNode as JsonObject;
        Console.WriteLine($"WorkflowInstanceId: {workflowInstanceId}");
        Console.WriteLine($"CheckpointRunId: {checkpointRunId}");
        Console.WriteLine($"CheckpointId: {checkpointId}");
        Console.WriteLine($"TopLevelProperties: {(payloadObject is null ? "<non-object>" : string.Join(", ", payloadObject.Select(property => property.Key)))}");
        return 0;
    }
}
