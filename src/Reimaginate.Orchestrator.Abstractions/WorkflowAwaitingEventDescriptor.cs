namespace Reimaginate.Orchestrator.Abstractions;

public sealed class WorkflowAwaitingEventDescriptor
{
    public string TaskName { get; set; } = null!;
    public string EventType { get; set; } = "*";
    public string FilterExpression { get; set; } = "true";
    public List<WorkflowCorrelationKeyDefinition>? CorrelationKeySpec { get; set; }
    public string? CheckpointId { get; set; }
}
