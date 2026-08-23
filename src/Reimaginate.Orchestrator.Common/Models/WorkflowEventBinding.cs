using System.Text.Json.Nodes;
using Reimaginate.Orchestrator.Common.Constants;

namespace Reimaginate.Orchestrator.Common.Models;

public sealed class WorkflowEventBinding
{
    public required string EventType { get; init; }
    public required string WorkflowType { get; init; }
    public JsonObject? InputTemplate { get; init; }
    public string? Filter { get; init; }
    public string? EventTypePath { get; init; }
    public WorkflowEventBindingMode Mode { get; init; } = WorkflowEventBindingMode.Start;
    public WorkflowEventCorrelationBinding? Correlation { get; init; }
}
