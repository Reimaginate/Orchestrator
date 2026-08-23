using System.Text.Json.Nodes;
using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowEventNormalization;

public interface IWorkflowEventMessageNormalizationService
{
    WorkflowEventMessage Normalize(BinaryData body, string? messageId, IReadOnlyDictionary<string, object?>? properties);

    JsonObject ToCloudEventPayload(WorkflowEventMessage message);
}
