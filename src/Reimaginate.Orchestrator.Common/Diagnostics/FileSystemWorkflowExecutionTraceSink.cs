using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.Common.Diagnostics;

public sealed class FileSystemWorkflowExecutionTraceSink(IWorkflowDiagnosticsPathResolver pathResolver) : IWorkflowExecutionTraceSink
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly IWorkflowDiagnosticsPathResolver _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));

    public bool IsEnabled => _pathResolver.IsEnabled;

    public string? GetDiagnosticsPath(string workflowInstanceId) => _pathResolver.TryGetWorkflowSessionPath(workflowInstanceId);

    public string? GetWorkflowTypeGraphPath(string workflowType) => _pathResolver.TryGetWorkflowTypeGraphPath(workflowType);

    public async ValueTask RecordAsync(string workflowInstanceId, string workflowType, string eventType, JsonObject? payload, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return;
        }

        var sessionPath = GetDiagnosticsPath(workflowInstanceId);
        if (string.IsNullOrWhiteSpace(sessionPath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(sessionPath);
            var timelinePath = Path.Combine(sessionPath, "timeline.jsonl");
            var envelope = new JsonObject
            {
                ["timestampUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["eventType"] = eventType,
                ["workflowType"] = workflowType,
                ["workflowInstanceId"] = workflowInstanceId,
                ["payload"] = payload?.DeepClone()
            };

            await AppendLineAsync(timelinePath, envelope.ToJsonString(), cancellationToken);
        }
        catch
        {
        }
    }

    public async ValueTask WriteGraphAsync(string workflowType, JsonObject graph, string? workflowInstanceId = null, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return;
        }

        try
        {
            var graphPath = GetWorkflowTypeGraphPath(workflowType);
            if (!string.IsNullOrWhiteSpace(graphPath))
            {
                await WriteJsonAsync(graphPath, graph, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(workflowInstanceId))
            {
                var sessionPath = GetDiagnosticsPath(workflowInstanceId);
                if (!string.IsNullOrWhiteSpace(sessionPath))
                {
                    await WriteJsonAsync(Path.Combine(sessionPath, "graph.json"), graph, cancellationToken);
                }
            }
        }
        catch
        {
        }
    }

    public async ValueTask WriteWorkflowInstanceSnapshotAsync(WorkflowInstance workflowInstance, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || workflowInstance is null || string.IsNullOrWhiteSpace(workflowInstance.Id))
        {
            return;
        }

        try
        {
            var sessionPath = GetDiagnosticsPath(workflowInstance.Id);
            if (string.IsNullOrWhiteSpace(sessionPath))
            {
                return;
            }

            var snapshot = JsonSerializer.SerializeToNode(workflowInstance, SerializerOptions) as JsonObject ?? new JsonObject();
            await WriteJsonAsync(Path.Combine(sessionPath, "instance.snapshot.json"), snapshot, cancellationToken);
        }
        catch
        {
        }
    }

    public async ValueTask WriteCheckpointSummaryAsync(string workflowInstanceId, string workflowType, JsonObject checkpointSummary, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return;
        }

        var sessionPath = GetDiagnosticsPath(workflowInstanceId);
        if (string.IsNullOrWhiteSpace(sessionPath))
        {
            return;
        }

        try
        {
            var checkpointsPath = Path.Combine(sessionPath, "checkpoints");
            Directory.CreateDirectory(checkpointsPath);

            var checkpointId = checkpointSummary["checkpointId"]?.GetValue<string>()
                ?? checkpointSummary["CheckpointId"]?.GetValue<string>()
                ?? "latest";
            var runId = checkpointSummary["runId"]?.GetValue<string>()
                ?? checkpointSummary["RunId"]?.GetValue<string>()
                ?? "run";
            var fileName = $"{WorkflowDiagnosticsPathResolver.SanitizeSegment(runId)}__{WorkflowDiagnosticsPathResolver.SanitizeSegment(checkpointId)}.json";
            var latestPath = Path.Combine(checkpointsPath, "latest.json");
            var summaryPath = Path.Combine(checkpointsPath, fileName);

            await WriteJsonAsync(summaryPath, checkpointSummary, cancellationToken);
            await WriteJsonAsync(latestPath, checkpointSummary, cancellationToken);
            await RecordAsync(workflowInstanceId, workflowType, "checkpoint.summary_written", new JsonObject
            {
                ["runId"] = runId,
                ["checkpointId"] = checkpointId,
                ["summaryPath"] = summaryPath
            }, cancellationToken);
        }
        catch
        {
        }
    }

    private async Task WriteJsonAsync(string path, JsonNode node, CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var content = node.ToJsonString(SerializerOptions);
        var fileLock = _fileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync(cancellationToken);
        try
        {
            await File.WriteAllTextAsync(path, content, Encoding.UTF8, cancellationToken);
        }
        finally
        {
            fileLock.Release();
        }
    }

    private async Task AppendLineAsync(string path, string line, CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var fileLock = _fileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(path, line + Environment.NewLine, Encoding.UTF8, cancellationToken);
        }
        finally
        {
            fileLock.Release();
        }
    }
}
