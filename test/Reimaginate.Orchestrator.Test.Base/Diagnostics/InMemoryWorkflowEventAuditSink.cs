using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Reimaginate.Orchestrator.Common.Services.WorkflowEventReceivers;

namespace Reimaginate.Orchestrator.Test.Base.Diagnostics;

public sealed class InMemoryWorkflowEventAuditSink : IWorkflowEventAuditSink
{
    private readonly ConcurrentQueue<JsonObject> _events = new();

    public void RecordReceivedEvent(JsonObject payload)
    {
        if (payload is null)
        {
            return;
        }

        _events.Enqueue((JsonObject)payload.DeepClone());
    }

    public IReadOnlyList<JsonObject> Snapshot()
    {
        return _events.Select(x => (JsonObject)x.DeepClone()).ToArray();
    }

    public void Clear()
    {
        while (_events.TryDequeue(out _))
        {
        }
    }
}
