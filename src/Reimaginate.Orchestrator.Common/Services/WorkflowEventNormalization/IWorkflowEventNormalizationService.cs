using System.Text.Json.Nodes;
using Reimaginate.Orchestrator.Common.Models;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowEventNormalization;

public interface IWorkflowEventNormalizationService
{
    ProcessEventEnvelope BuildNormalizedEventEnvelope(JsonObject payload, WorkflowEventBinding binding);

    JsonObject BuildWorkflowInput(ProcessEventEnvelope envelope, WorkflowEventBinding binding);

    bool MatchesBindingFilter(WorkflowEventBinding binding, ProcessEventEnvelope envelope);

    IReadOnlyCollection<string> ExtractEventTypeCandidates(JsonObject payload, IEnumerable<string>? configuredEventTypePaths = null);
}
