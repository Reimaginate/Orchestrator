namespace Reimaginate.Orchestrator.Abstractions;

public sealed class WorkflowEventTriggerBindingDescriptor
{
    public required string EventType { get; init; }
    public required string WorkflowType { get; init; }
    public string? Filter { get; init; }
    public string? EventTypePath { get; init; }
    public string? Mode { get; init; }
}
