namespace Reimaginate.Orchestrator.Abstractions;

public sealed class WorkflowResolvedCorrelationKey
{
    public required string Name { get; init; }
    public required string Value { get; init; }
}
