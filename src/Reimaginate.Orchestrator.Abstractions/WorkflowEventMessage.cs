using System.Text.Json.Nodes;

namespace Reimaginate.Orchestrator.Abstractions;

public sealed record WorkflowEventMessage
{
    public required string SpecVersion { get; init; }

    public required string Id { get; init; }

    public required string Source { get; init; }

    public required string Type { get; init; }

    public string? Subject { get; init; }

    public DateTimeOffset? Time { get; init; }

    public string? DataContentType { get; init; }

    public string? DataSchema { get; init; }

    public required JsonNode Data { get; init; }

    public IReadOnlyDictionary<string, object?> Extensions { get; init; } = new Dictionary<string, object?>();
}
