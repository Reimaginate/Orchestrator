using System.Text.Json.Nodes;

namespace Reimaginate.Orchestrator.Common.Executors;

internal sealed class WorkflowRaisedException(
    string errorType,
    string? message,
    string taskName,
    JsonObject? data = null) : Exception(string.IsNullOrWhiteSpace(message) ? $"Workflow raise '{errorType}' emitted by task '{taskName}'." : message)
{
    public string ErrorType { get; } = string.IsNullOrWhiteSpace(errorType) ? "WorkflowRaiseError" : errorType;

    public string TaskName { get; } = taskName;

    public JsonObject? WorkflowData { get; } = data?.DeepClone() as JsonObject;
}
