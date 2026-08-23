using System.Text.Json.Nodes;

namespace Reimaginate.Orchestrator.Common.Diagnostics;

internal static class WorkflowExecutionTraceContext
{
    internal sealed record Snapshot(string WorkflowType, string WorkflowInstanceId);

    private sealed class ScopeState
    {
        public required IWorkflowExecutionTraceSink Sink { get; init; }
        public required string WorkflowType { get; init; }
        public required string WorkflowInstanceId { get; init; }
        public ScopeState? Parent { get; init; }
        public HashSet<string> ActiveHaltingTaskNames { get; } = new(StringComparer.Ordinal);
    }

    private sealed class ScopeToken(Action onDispose) : IDisposable
    {
        private readonly Action _onDispose = onDispose;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _onDispose();
            }
        }
    }

    private static readonly AsyncLocal<ScopeState?> CurrentScope = new();

    public static IDisposable BeginScope(IWorkflowExecutionTraceSink sink, string workflowType, string workflowInstanceId)
    {
        var previous = CurrentScope.Value;
        CurrentScope.Value = new ScopeState
        {
            Sink = sink,
            WorkflowType = workflowType,
            WorkflowInstanceId = workflowInstanceId,
            Parent = previous
        };

        return new ScopeToken(() => CurrentScope.Value = previous);
    }

    public static Snapshot? GetCurrentSnapshot()
    {
        var current = CurrentScope.Value;
        return current is null
            ? null
            : new Snapshot(current.WorkflowType, current.WorkflowInstanceId);
    }

    public static void AddActiveHaltingTaskName(string taskName)
    {
        var current = CurrentScope.Value;
        if (current is null || string.IsNullOrWhiteSpace(taskName))
        {
            return;
        }

        current.ActiveHaltingTaskNames.Add(taskName);
    }

    public static IReadOnlyCollection<string> GetActiveHaltingTaskNames()
    {
        var current = CurrentScope.Value;
        return current is null
            ? []
            : current.ActiveHaltingTaskNames.ToArray();
    }

    public static ValueTask RecordAsync(string eventType, JsonObject? payload, CancellationToken cancellationToken = default)
    {
        var current = CurrentScope.Value;
        if (current is null || !current.Sink.IsEnabled)
        {
            return ValueTask.CompletedTask;
        }

        return current.Sink.RecordAsync(current.WorkflowInstanceId, current.WorkflowType, eventType, payload, cancellationToken);
    }
}
