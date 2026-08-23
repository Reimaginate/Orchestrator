using System.Text.Json.Nodes;

namespace Reimaginate.Orchestrator.Abstractions;

public sealed record WorkflowReceivedMessage
{
    public required string ReceiptHandle { get; init; }

    public required JsonNode Payload { get; init; }
}
