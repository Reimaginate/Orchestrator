using System.Text.Json.Nodes;

namespace Reimaginate.Orchestrator.Common.Models;

public sealed class WorkflowEvent
{
    public string? EventId { get; set; }
    public string? EventType { get; set; }
    public string? EventSource { get; set; }
    public JsonNode? Payload { get; set; }
}
