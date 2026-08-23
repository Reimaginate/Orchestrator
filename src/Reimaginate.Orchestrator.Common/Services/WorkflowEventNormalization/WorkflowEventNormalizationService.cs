using System.Text.Json.Nodes;
using Reimaginate.Orchestrator.Common.Helpers;
using Reimaginate.Orchestrator.Common.Models;
using Reimaginate.Orchestrator.Common.Services.WorkflowCorrelation;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowEventNormalization;

public class WorkflowEventNormalizationService(
    IWorkflowCorrelationService workflowCorrelationService,
    IWorkflowEnvironmentProvider workflowEnvironmentProvider) : IWorkflowEventNormalizationService
{
    #region Event envelope and workflow input normalization

    // Build a normalized event envelope from the incoming payload and enrich it with configured correlation keys.
    public ProcessEventEnvelope BuildNormalizedEventEnvelope(JsonObject payload, WorkflowEventBinding binding)
    {
        var authoringPayload = WorkflowEventPayloadContextHelper.BuildTriggerAuthoringPayload(payload);
        var envelope = new ProcessEventEnvelope
        {
            EventType = TryResolveEventType(payload),
            EventId = TryResolveEventId(payload),
            EntityType = TryResolveContextValue(payload, "entityType", "resourceType", "EntityType") ?? binding.EventTypePath,
            EntityId = TryResolveContextValue(payload, "entityId", "resourceId", "EntityId", "id"),
            EventSource = TryResolveContextValue(payload, "eventSource", "source", "EventSource"),
            EventSequence = TryResolveContextValue(payload, "eventSequence", "sequence", "EventSequence", "sequenceNumber"),
            EntityVersion = TryResolveLongValue(payload, "entityVersion", "version", "EntityVersion"),
            EventTimestamp = TryResolveDateTimeOffsetValue(payload, "eventTimestamp", "timestamp", "updatedAt", "EventTimestamp"),
            RawPayload = (JsonObject)payload.DeepClone(),
            NormalizedPayload = payload,
            Payload = payload,
            AuthoringPayload = authoringPayload,
            AwaitingFilterPayload = payload
        };

        envelope.CorrelationKeys = workflowCorrelationService.ResolveCorrelationKeys(binding.Correlation, envelope);
        envelope.EntityType = envelope.CorrelationKeys.FirstOrDefault(x => string.Equals(x.Name, "entityType", StringComparison.OrdinalIgnoreCase))?.Value ?? envelope.EntityType;
        envelope.EntityId = envelope.CorrelationKeys.FirstOrDefault(x => string.Equals(x.Name, "entityId", StringComparison.OrdinalIgnoreCase))?.Value ?? envelope.EntityId;

        return envelope;
    }

    // Transform the incoming payload into workflow input using the binding input template.
    public JsonObject BuildWorkflowInput(ProcessEventEnvelope envelope, WorkflowEventBinding binding)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(envelope.AuthoringPayload);

        return WorkflowEventInputTransformer.Transform(envelope.AuthoringPayload, binding.InputTemplate, environment: workflowEnvironmentProvider.BuildEnvironment(binding.WorkflowType));
    }

    #endregion

    #region Binding evaluation and event-type candidate discovery

    // Evaluate whether the normalized payload satisfies the binding's filter expression.
    public bool MatchesBindingFilter(WorkflowEventBinding binding, ProcessEventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(envelope.AuthoringPayload);

        return WorkflowConditionEvaluator.Evaluate(binding.Filter, envelope.AuthoringPayload, workflowEnvironmentProvider.BuildEnvironment(binding.WorkflowType));
    }

    // Resolve all likely event-type values from default and configured event-type paths.
    public IReadOnlyCollection<string> ExtractEventTypeCandidates(JsonObject payload, IEnumerable<string>? configuredEventTypePaths = null)
    {
        var eventTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddEventTypeCandidate(eventTypes, ExtractJsonString(payload["type"]));
        AddEventTypeCandidate(eventTypes, ExtractJsonString(payload["eventType"]));
        AddEventTypeCandidate(eventTypes, ExtractJsonString(JsonPath.SelectToken(payload, "$.data.EventType")));

        var defaultEventType = TryResolveEventType(payload);
        AddEventTypeCandidate(eventTypes, defaultEventType);

        if (configuredEventTypePaths is null)
        {
            return eventTypes;
        }

        foreach (var eventTypePath in configuredEventTypePaths.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var token = SelectTokenWithCloudEventDataFallback(payload, eventTypePath!);
            if (token is null)
            {
                continue;
            }

            var resolvedEventType = token switch
            {
                JsonValue value when value.TryGetValue<string>(out var text) => text,
                _ => token.ToJsonString().Trim('"')
            };

            AddEventTypeCandidate(eventTypes, resolvedEventType);
        }

        return eventTypes;
    }

    #endregion

    #region Event-type extraction helpers

    private static void AddEventTypeCandidate(ISet<string> candidates, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            candidates.Add(value);
        }
    }

    private static JsonNode? SelectTokenWithCloudEventDataFallback(JsonObject payload, string path)
    {
        var token = JsonPath.SelectToken(payload, path);
        if (token is not null)
        {
            return token;
        }

        if (path.StartsWith("$.data.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "$.data", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var dataPath = path.StartsWith("$.", StringComparison.Ordinal)
            ? "$.data" + path[1..]
            : "$.data." + path.TrimStart('.');

        return JsonPath.SelectToken(payload, dataPath);
    }

    #endregion

    #region Context and primitive value resolution helpers

    private static string? TryResolveEventType(JsonObject payload)
    {
        return TryResolveContextValue(payload, "type", "eventType", "EventType", "action", "Action");
    }

    private static string? TryResolveEventId(JsonObject payload)
    {
        return TryResolveContextValue(payload, "id", "eventId", "EventId");
    }

    private static string? TryResolveContextValue(JsonObject payload, params string[] candidateKeys)
    {
        foreach (var candidateKey in candidateKeys)
        {
            var token = JsonPath.SelectToken(payload, candidateKey)
                        ?? JsonPath.SelectToken(payload, $"$.{candidateKey}")
                        ?? JsonPath.SelectToken(payload, $"$.data.{candidateKey}")
                        ?? JsonPath.SelectToken(payload, $"$.context.{candidateKey}");

            if (token is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
            {
                return NormalizePossibleJsonString(text);
            }
        }

        return null;
    }

    private static string? ExtractJsonString(JsonNode? node)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return NormalizePossibleJsonString(text);
    }

    private static string NormalizePossibleJsonString(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed[1..^1]
            : trimmed;
    }

    private static long? TryResolveLongValue(JsonObject payload, params string[] candidateKeys)
    {
        var value = TryResolveContextValue(payload, candidateKeys);
        return long.TryParse(value, out var longValue) ? longValue : null;
    }

    private static DateTimeOffset? TryResolveDateTimeOffsetValue(JsonObject payload, params string[] candidateKeys)
    {
        var value = TryResolveContextValue(payload, candidateKeys);
        return DateTimeOffset.TryParse(value, out var timestamp) ? timestamp : null;
    }

    #endregion
}
