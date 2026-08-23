using System.Text.Json.Nodes;
using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.Common.Diagnostics;

public sealed class NullWorkflowExecutionTraceSink : IWorkflowExecutionTraceSink
{
    public bool IsEnabled => false;

    public string? GetDiagnosticsPath(string workflowInstanceId) => null;
    public string? GetWorkflowTypeGraphPath(string workflowType) => null;
    public ValueTask RecordAsync(string workflowInstanceId, string workflowType, string eventType, JsonObject? payload, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask WriteGraphAsync(string workflowType, JsonObject graph, string? workflowInstanceId = null, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask WriteWorkflowInstanceSnapshotAsync(WorkflowInstance workflowInstance, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask WriteCheckpointSummaryAsync(string workflowInstanceId, string workflowType, JsonObject checkpointSummary, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
