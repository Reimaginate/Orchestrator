using System.Text.Json.Nodes;
using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.Common.Models;

public sealed class ProcessEventEnvelope
{
    public string? EventId { get; set; }
    public string? EventType { get; set; }
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? EventSource { get; set; }
    public string? EventSequence { get; set; }
    public long? EntityVersion { get; set; }
    public DateTimeOffset? EventTimestamp { get; set; }
    public JsonObject RawPayload { get; set; } = new();
    public JsonObject NormalizedPayload { get; set; } = new();
    public JsonObject Payload { get; set; } = new();
    public JsonObject AuthoringPayload { get; set; } = new();
    public JsonObject AwaitingFilterPayload { get; set; } = new();
    public IReadOnlyList<WorkflowResolvedCorrelationKey> CorrelationKeys { get; set; } = [];
}
