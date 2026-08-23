namespace Reimaginate.Orchestrator.Abstractions;

public class WorkflowCorrelationLink
{
    public string LinkType { get; set; } = null!;
    public string EntityId { get; set; } = null!;
    public string? EventType { get; set; }
    public DateTimeOffset? CreatedOn { get; set; }
    public DateTimeOffset? LastUpdated { get; set; }
    public DateTimeOffset? LastProcessedEventTimestamp { get; set; }
    public long? LastProcessedEventVersion { get; set; }
}
