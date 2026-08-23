using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.Common.Models;

public sealed class WorkflowCorrelationResolution
{
    public IReadOnlyList<WorkflowResolvedCorrelationKey> CorrelationKeys { get; init; } = [];
    public string CorrelationKey { get; init; } = null!;
    public IReadOnlyList<WorkflowInstance> WorkflowInstances { get; init; } = [];
    public WorkflowInstance? WorkflowInstance { get; init; }
}
