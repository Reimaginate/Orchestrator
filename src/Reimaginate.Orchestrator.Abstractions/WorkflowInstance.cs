using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reimaginate.Orchestrator.Abstractions;

public class WorkflowInstance
{
    public string Id { get; set; } = null!;
    public string? ConcurrencyToken { get; set; }
    public string WorkflowType { get; set; } = null!;
    public string Status { get; set; } = null!;
    public string? FailureReason { get; set; }
    public WorkflowExecutionFailureDetails? FailureDetails { get; set; }
    public DateTimeOffset? FailedOn { get; set; }
    public string? CurrentCheckpointId { get; set; }
    public string? WorkflowInstanceId { get; set; }
    public List<WorkflowResolvedCorrelationKey> CorrelationKeys { get; set; } = [];
    public List<WorkflowCorrelationLink> Correlations { get; set; } = [];
    public List<WorkflowProcessedEventRecord> ProcessedEvents { get; set; } = [];
    public List<WorkflowAwaitingEventDescriptor> AwaitingEvents { get; set; } = [];
    public Dictionary<string, string> Metadata { get; set; } = [];
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    public string? OriginatingEventId { get; set; }
    public string? OriginatingEventType { get; set; }
    public string? OriginatingEventSource { get; set; }
    public DateTimeOffset? LastUpdated { get; set; }
    public DateTimeOffset? CreatedOn { get; set; }
}
