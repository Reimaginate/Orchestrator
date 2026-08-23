using System.Text.Json.Nodes;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowEventReceivers;

public interface IWorkflowEventAuditSink
{
    void RecordReceivedEvent(JsonObject payload);
    IReadOnlyList<JsonObject> Snapshot();
    void Clear();
}
