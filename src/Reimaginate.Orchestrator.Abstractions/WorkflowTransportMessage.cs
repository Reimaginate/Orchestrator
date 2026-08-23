namespace Reimaginate.Orchestrator.Abstractions;

public sealed record WorkflowTransportMessage
{
    public required BinaryData Body { get; init; }

    public string? MessageId { get; init; }

    public IReadOnlyDictionary<string, object?> ApplicationProperties { get; init; } = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
}
