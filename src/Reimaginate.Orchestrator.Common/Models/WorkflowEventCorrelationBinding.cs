using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.Common.Models;

public sealed class WorkflowEventCorrelationBinding
{
    public IReadOnlyList<WorkflowCorrelationKeyDefinition> Keys { get; init; } = [];
}
