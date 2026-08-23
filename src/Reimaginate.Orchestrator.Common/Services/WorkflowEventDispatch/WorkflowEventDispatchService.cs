using System.Text.Json.Nodes;
using Reimaginate.Orchestrator.Common.Constants;
using Reimaginate.Orchestrator.Common.Models;
using Reimaginate.Orchestrator.Common.Services.WorkflowEventBindings;
using Reimaginate.Orchestrator.Common.Services.WorkflowEventNormalization;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowEventDispatch;

public sealed class WorkflowEventDispatchService(
    IWorkflowEventBindingService workflowEventBindingService,
    IWorkflowEventNormalizationService workflowEventNormalizationService) : IWorkflowEventDispatchService
{
    #region Resolve listening workflow contexts

    public IReadOnlyList<ListeningWorkflowTypeContext> ResolveListeningWorkflows(
        IReadOnlyDictionary<string, IReadOnlyList<WorkflowEventBinding>> workflowEventMap,
        JsonObject payload)
    {
        var eventTypePaths = workflowEventMap.Values
            .SelectMany(x => x)
            .Select(x => x.EventTypePath)
            .OfType<string>()
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var eventTypes = workflowEventNormalizationService.ExtractEventTypeCandidates(payload, eventTypePaths);
        var listeningBindings = workflowEventBindingService.ResolveListeningWorkflowEventBindings(workflowEventMap, eventTypes);

        return BuildDispatchContexts(payload, listeningBindings);
    }

    public async Task<IReadOnlyList<ListeningWorkflowTypeContext>> ResolveListeningWorkflowsAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        var eventTypes = workflowEventNormalizationService.ExtractEventTypeCandidates(payload);
        var listeningBindings = await workflowEventBindingService.ResolveListeningWorkflowEventBindingsAsync(eventTypes, cancellationToken);

        return BuildDispatchContexts(payload, listeningBindings);
    }

    private IReadOnlyList<ListeningWorkflowTypeContext> BuildDispatchContexts(JsonObject payload, IReadOnlyCollection<WorkflowEventBinding> listeningBindings)
    {
        var dispatchContexts = new List<ListeningWorkflowTypeContext>();

        foreach (var workflowBindings in listeningBindings.GroupBy(x => x.WorkflowType, StringComparer.OrdinalIgnoreCase))
        {
            var matches = new List<(WorkflowEventBinding Binding, ProcessEventEnvelope EventEnvelope)>();

            foreach (var workflowBinding in workflowBindings)
            {
                var normalizedEvent = workflowEventNormalizationService.BuildNormalizedEventEnvelope(payload, workflowBinding);
                if (!workflowEventNormalizationService.MatchesBindingFilter(workflowBinding, normalizedEvent))
                {
                    continue;
                }

                normalizedEvent.AwaitingFilterPayload = normalizedEvent.NormalizedPayload;
                matches.Add((workflowBinding, normalizedEvent));
            }

            if (matches.Count == 0)
            {
                continue;
            }

            var primaryMatch = matches[0];
            var canResume = matches.Any(x => x.Binding.Mode.HasFlag(WorkflowEventBindingMode.Resume));
            var canStart = matches.Any(x => x.Binding.Mode.HasFlag(WorkflowEventBindingMode.Start));
            var startInput = workflowEventNormalizationService.BuildWorkflowInput(primaryMatch.EventEnvelope, primaryMatch.Binding);

            dispatchContexts.Add(new ListeningWorkflowTypeContext(
                primaryMatch.Binding.WorkflowType,
                matches,
                canResume,
                canStart,
                startInput));
        }

        return dispatchContexts;
    }

    #endregion
}
