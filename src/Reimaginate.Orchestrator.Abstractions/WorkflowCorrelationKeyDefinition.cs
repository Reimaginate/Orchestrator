namespace Reimaginate.Orchestrator.Abstractions;

public sealed class WorkflowCorrelationKeyDefinition
{
    public required string Name { get; init; }
    public string? Path { get; init; }
    public bool Required { get; init; }
    public string? Normalizer { get; init; }
}
