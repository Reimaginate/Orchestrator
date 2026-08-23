using System.Text.Json.Nodes;
using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.Common.Diagnostics;

public interface IWorkflowExecutionTraceSink
{
    bool IsEnabled { get; }
    string? GetDiagnosticsPath(string workflowInstanceId);
    string? GetWorkflowTypeGraphPath(string workflowType);
    ValueTask RecordAsync(string workflowInstanceId, string workflowType, string eventType, JsonObject? payload, CancellationToken cancellationToken = default);
    ValueTask WriteGraphAsync(string workflowType, JsonObject graph, string? workflowInstanceId = null, CancellationToken cancellationToken = default);
    ValueTask WriteWorkflowInstanceSnapshotAsync(WorkflowInstance workflowInstance, CancellationToken cancellationToken = default);
    ValueTask WriteCheckpointSummaryAsync(string workflowInstanceId, string workflowType, JsonObject checkpointSummary, CancellationToken cancellationToken = default);
}
