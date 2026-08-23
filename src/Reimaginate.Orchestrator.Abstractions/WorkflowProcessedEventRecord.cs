namespace Reimaginate.Orchestrator.Abstractions;

public class WorkflowProcessedEventRecord
{
    public string Key { get; set; } = null!;
    public string? Action { get; set; }
    public string? WorkflowStatusAfterAction { get; set; }
    public string? EventId { get; set; }
    public string? EventSource { get; set; }
    public string? EventSequence { get; set; }
    public string? EntityId { get; set; }
    public DateTimeOffset ProcessedOn { get; set; }
}
