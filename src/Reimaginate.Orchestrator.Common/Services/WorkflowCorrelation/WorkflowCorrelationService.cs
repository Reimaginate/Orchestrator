using System.Text.Json.Nodes;
using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.Common.Constants;
using Reimaginate.Orchestrator.Common.Helpers;
using Reimaginate.Orchestrator.Common.Models;
using Reimaginate.Orchestrator.Common.Services.WorkflowInstanceMutation;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowCorrelation;

public class WorkflowCorrelationService(
    IWorkflowInstanceStore workflowInstanceStore,
    IWorkflowInstanceMutationService workflowInstanceMutationService,
    IWorkflowEnvironmentProvider workflowEnvironmentProvider) : IWorkflowCorrelationService
{
    #region Correlation key defaults

    private static readonly WorkflowCorrelationKeyDefinition[] DefaultDefinitions =
    [
        new() { Name = "entityType", Path = "entityType", Normalizer = "trim|lower" },
        new() { Name = "entityId", Path = "entityId", Required = true, Normalizer = "trim|lower" }
    ];

    #endregion

    #region Correlation key resolution

    public IReadOnlyList<WorkflowResolvedCorrelationKey> ResolveCorrelationKeys(WorkflowEventCorrelationBinding? correlationBinding, ProcessEventEnvelope envelope)
    {
        // Prefer binding-specific keys when supplied, otherwise fallback to the default entity correlation shape.
        var definitions = correlationBinding?.Keys?.Count > 0
            ? correlationBinding.Keys
            : DefaultDefinitions;

        var keySet = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in definitions)
        {
            if (string.IsNullOrWhiteSpace(definition.Name))
            {
                continue;
            }

            var rawValue = ResolveRawValue(definition, envelope);
            var normalizedValue = ApplyNormalizer(rawValue, definition.Normalizer);

            if (string.IsNullOrWhiteSpace(normalizedValue))
            {
                if (definition.Required)
                {
                    return [];
                }

                continue;
            }

            keySet[definition.Name] = normalizedValue;
        }

        return keySet.Select(x => new WorkflowResolvedCorrelationKey { Name = x.Key, Value = x.Value }).ToArray();
    }

    public async Task<WorkflowCorrelationResolution> ResolveRunningInstancesAsync(string workflowType, IReadOnlyList<WorkflowResolvedCorrelationKey> correlationKeys, CancellationToken cancellationToken)
        => await ResolveInstancesAsync(workflowType, correlationKeys,
        [
            WorkflowInstanceStatuses.Running,
            WorkflowInstanceStatuses.Suspended,
            WorkflowInstanceStatuses.Failed
        ], cancellationToken);

    public async Task<WorkflowCorrelationResolution> ResolveInstancesAsync(
        string workflowType,
        IReadOnlyList<WorkflowResolvedCorrelationKey> correlationKeys,
        IReadOnlyCollection<string> statuses,
        CancellationToken cancellationToken)
    {
        // De-duplicate and clean incoming keys to avoid generating equivalent lookups for the same name.
        var normalizedKeys = correlationKeys
            .Where(x => !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.Value))
            .DistinctBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var correlationKey = BuildCorrelationKey(workflowType, normalizedKeys);

        if (normalizedKeys.Length == 0)
        {
            return new WorkflowCorrelationResolution
            {
                CorrelationKey = correlationKey,
                CorrelationKeys = normalizedKeys
            };
        }

        var findResult = await workflowInstanceStore.FindByCorrelationAsync(
            normalizedKeys,
            statuses,
            cancellationToken);

        if (!findResult.Success)
        {
            throw new InvalidOperationException(findResult.FailureReason ?? "Failed to resolve workflow correlation.");
        }

        var workflowInstances = findResult.WorkflowInstances
            .Where(x => string.Equals(x.WorkflowType, workflowType, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return new WorkflowCorrelationResolution
        {
            CorrelationKey = correlationKey,
            CorrelationKeys = normalizedKeys,
            WorkflowInstances = workflowInstances,
            WorkflowInstance = workflowInstances.FirstOrDefault()
        };
    }

    #endregion

    #region Workflow-instance correlation lifecycle

    public async Task EnsureWorkflowCorrelationAsync(string workflowInstanceId, ProcessEventEnvelope normalizedEvent, CancellationToken cancellationToken)
    {
        if (normalizedEvent.CorrelationKeys.Count == 0)
        {
            return;
        }

        var mutationResult = await workflowInstanceMutationService.UpsertAsync(
            workflowInstanceId,
            (workflowInstance, utcNow) =>
            {
                if (workflowInstance is null)
                {
                    throw new InvalidOperationException($"Failed to load workflow instance '{workflowInstanceId}' for correlation registration.");
                }

                workflowInstance.Correlations ??= [];
                workflowInstance.CorrelationKeys ??= [];

                foreach (var key in normalizedEvent.CorrelationKeys)
                {
                    if (workflowInstance.CorrelationKeys.Any(existing => string.Equals(existing.Name, key.Name, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(existing.Value, key.Value, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    workflowInstance.CorrelationKeys.Add(new WorkflowResolvedCorrelationKey { Name = key.Name, Value = key.Value });
                }

                var linkType = normalizedEvent.CorrelationKeys.FirstOrDefault(x => string.Equals(x.Name, "entityType", StringComparison.OrdinalIgnoreCase))?.Value ?? "event";
                var entityId = normalizedEvent.CorrelationKeys.FirstOrDefault(x => string.Equals(x.Name, "entityId", StringComparison.OrdinalIgnoreCase))?.Value;

                if (!string.IsNullOrWhiteSpace(entityId))
                {
                    var existingLink = workflowInstance.Correlations.FirstOrDefault(correlation =>
                        string.Equals(correlation.LinkType, linkType, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(correlation.EntityId, entityId, StringComparison.OrdinalIgnoreCase));

                    if (existingLink is null)
                    {
                        workflowInstance.Correlations.Add(new WorkflowCorrelationLink
                        {
                            LinkType = linkType,
                            EntityId = entityId,
                            EventType = normalizedEvent.EventType,
                            CreatedOn = utcNow,
                            LastUpdated = utcNow,
                            LastProcessedEventTimestamp = normalizedEvent.EventTimestamp,
                            LastProcessedEventVersion = normalizedEvent.EntityVersion
                        });
                    }
                    else
                    {
                        existingLink.LastUpdated = utcNow;
                        existingLink.EventType ??= normalizedEvent.EventType;
                        if (normalizedEvent.EventTimestamp.HasValue)
                        {
                            existingLink.LastProcessedEventTimestamp = normalizedEvent.EventTimestamp;
                        }

                        if (normalizedEvent.EntityVersion.HasValue)
                        {
                            existingLink.LastProcessedEventVersion = normalizedEvent.EntityVersion;
                        }
                    }
                }

                workflowInstance.LastUpdated = utcNow;
                return ValueTask.FromResult(WorkflowInstanceMutationCommand.Write(workflowInstance));
            },
            cancellationToken);

        if (!mutationResult.Success)
        {
            throw new InvalidOperationException(mutationResult.FailureReason ?? $"Failed to persist workflow correlation for instance '{workflowInstanceId}'.");
        }
    }

    public WorkflowCorrelationLink? FindCorrelationLink(WorkflowInstance workflowInstance, ProcessEventEnvelope envelope)
    {
        // Resolve entity identity from explicit correlation keys first, then fallback to envelope-level fields.
        var entityId = envelope.CorrelationKeys.FirstOrDefault(x => string.Equals(x.Name, "entityId", StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(entityId))
        {
            entityId = envelope.EntityId;
        }

        if (string.IsNullOrWhiteSpace(entityId))
        {
            return null;
        }

        var entityType = envelope.CorrelationKeys.FirstOrDefault(x => string.Equals(x.Name, "entityType", StringComparison.OrdinalIgnoreCase))?.Value ?? envelope.EntityType;

        return workflowInstance.Correlations.FirstOrDefault(correlation =>
            string.Equals(correlation.EntityId, entityId, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(entityType)
                || string.Equals(correlation.LinkType, entityType, StringComparison.OrdinalIgnoreCase)));
    }

    public async Task<bool> RegisterWorkflowCorrelationAsync(
        string workflowInstanceId,
        string workflowType,
        string entityType,
        string entityId,
        string correlationRole,
        CancellationToken cancellationToken)
    {
        var created = false;
        var mutationResult = await workflowInstanceMutationService.UpsertAsync(
            workflowInstanceId,
            (workflowInstance, utcNow) =>
            {
                if (workflowInstance is null)
                {
                    throw new InvalidOperationException("Workflow instance was not found.");
                }

                workflowInstance.WorkflowType = workflowType;
                workflowInstance.Correlations ??= [];

                var correlationExists = workflowInstance.Correlations.Any(correlation =>
                    string.Equals(correlation.LinkType, entityType, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(correlation.EntityId, entityId, StringComparison.OrdinalIgnoreCase));

                if (correlationExists)
                {
                    return ValueTask.FromResult(WorkflowInstanceMutationCommand.None());
                }

                created = true;
                workflowInstance.Correlations.Add(new WorkflowCorrelationLink
                {
                    LinkType = entityType,
                    EntityId = entityId,
                    EventType = correlationRole,
                    CreatedOn = utcNow,
                    LastUpdated = utcNow
                });

                workflowInstance.LastUpdated = utcNow;
                return ValueTask.FromResult(WorkflowInstanceMutationCommand.Write(workflowInstance));
            },
            cancellationToken);

        if (!mutationResult.Success)
        {
            throw new InvalidOperationException(mutationResult.FailureReason ?? "Failed to persist workflow correlation.");
        }

        return created;
    }

    #endregion

    #region Awaiting-event descriptor matching

    public IReadOnlyList<WorkflowAwaitingEventDescriptor> MatchAwaitingEventDescriptors(WorkflowInstance workflowInstance, ProcessEventEnvelope envelope)
    {
        if (workflowInstance.AwaitingEvents.Count == 0)
        {
            return [];
        }
        
        var listenContext = WorkflowEventPayloadContextHelper.BuildListenContext(envelope.Payload);

        return workflowInstance.AwaitingEvents
            .Where(awaitingEvent => MatchesAwaitingDescriptor(awaitingEvent, envelope.EventType, listenContext, envelope.CorrelationKeys, workflowEnvironmentProvider.BuildEnvironment(workflowInstance.WorkflowType)))
            .ToArray();
    }

    public void RemoveMatchedAwaitingDescriptors(
        WorkflowInstance instance,
        IReadOnlyCollection<WorkflowAwaitingEventDescriptor> matchedDescriptors)
    {
        if (instance.AwaitingEvents.Count == 0 || matchedDescriptors.Count == 0)
        {
            return;
        }

        var keys = matchedDescriptors
            .Select(BuildAwaitingDescriptorKey)
            .ToHashSet(StringComparer.Ordinal);

        instance.AwaitingEvents = instance.AwaitingEvents
            .Where(x => !keys.Contains(BuildAwaitingDescriptorKey(x)))
            .ToList();
    }

    #endregion

    #region Resume binding selection

    public (WorkflowEventBinding Binding, ProcessEventEnvelope Envelope) SelectResumeBindingMatch(
        IReadOnlyList<(WorkflowEventBinding Binding, ProcessEventEnvelope Envelope)> matches,
        WorkflowInstance instance)
    {
        var resumeBindings = matches
            .Where(x => x.Binding.Mode.HasFlag(WorkflowEventBindingMode.Resume))
            .ToList();

        if (resumeBindings.Count == 0)
        {
            return matches[0];
        }

        var instanceCorrelation = instance.CorrelationKeys
            .Where(x => !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.Value))
            .ToDictionary(x => x.Name, x => x.Value, StringComparer.OrdinalIgnoreCase);

        var selectedByCorrelation = resumeBindings.FirstOrDefault(x =>
            x.Envelope.CorrelationKeys.Count > 0
            && x.Envelope.CorrelationKeys.All(key =>
                instanceCorrelation.TryGetValue(key.Name, out var value)
                && string.Equals(value, key.Value, StringComparison.OrdinalIgnoreCase)));

        if (selectedByCorrelation.Binding is not null)
        {
            return selectedByCorrelation;
        }

        return resumeBindings[0];
    }

    #endregion

    #region Awaiting-event helper methods

    private static bool MatchesAwaitingDescriptor(
        WorkflowAwaitingEventDescriptor descriptor,
        string? actualEventType,
        JsonObject? payload,
        IReadOnlyCollection<WorkflowResolvedCorrelationKey>? eventCorrelationKeys,
        JsonObject? environment)
    {
        if (!MatchesEventType(descriptor.EventType, actualEventType))
        {
            return false;
        }

        if (!MatchesRequiredCorrelation(descriptor.CorrelationKeySpec, payload, eventCorrelationKeys))
        {
            return false;
        }

        if (payload is null)
        {
            return true;
        }

        return WorkflowConditionEvaluator.Evaluate(descriptor.FilterExpression, payload, environment);
    }

    private static bool MatchesEventType(string? expectedEventType, string? actualEventType)
    {
        if (string.IsNullOrWhiteSpace(expectedEventType) || string.Equals(expectedEventType, "*", StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(expectedEventType, actualEventType, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesRequiredCorrelation(
        IReadOnlyCollection<WorkflowCorrelationKeyDefinition>? correlationSpec,
        JsonNode? payload,
        IReadOnlyCollection<WorkflowResolvedCorrelationKey>? eventCorrelationKeys)
    {
        if (correlationSpec is null || correlationSpec.Count == 0)
        {
            return true;
        }

        foreach (var key in correlationSpec.Where(x => x.Required))
        {
            if (eventCorrelationKeys is not null)
            {
                var hasMatchingKey = eventCorrelationKeys.Any(x => string.Equals(x.Name, key.Name, StringComparison.OrdinalIgnoreCase));
                if (!hasMatchingKey)
                {
                    return false;
                }

                continue;
            }

            if (payload is null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(key.Path))
            {
                continue;
            }

            var lookupPath = key.Path.StartsWith("$", StringComparison.Ordinal)
                ? key.Path
                : $"$.{key.Path}";

            if (JsonPath.SelectToken(payload, lookupPath) is null)
            {
                return false;
            }
        }

        return true;
    }

    private static string BuildAwaitingDescriptorKey(WorkflowAwaitingEventDescriptor descriptor)
    {
        return $"{descriptor.TaskName}|{descriptor.EventType}|{descriptor.FilterExpression}|{descriptor.CheckpointId}";
    }

    #endregion

    #region Correlation-key helper methods

    // Build a deterministic key so correlation lookups and logging remain stable across call sites.
    private static string BuildCorrelationKey(string workflowType, IReadOnlyCollection<WorkflowResolvedCorrelationKey> keys)
    {
        if (keys.Count == 0)
        {
            return $"{workflowType}:event";
        }

        var serializedKeys = keys
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => $"{x.Name}={x.Value}");

        return $"{workflowType}:{string.Join('|', serializedKeys)}";
    }

    private static string? ResolveRawValue(WorkflowCorrelationKeyDefinition definition, ProcessEventEnvelope envelope)
    {
        if (TryResolveTopLevel(definition.Path, envelope, out var topLevel))
        {
            return topLevel;
        }

        if (TryResolveTopLevel(definition.Name, envelope, out topLevel))
        {
            return topLevel;
        }

        var payload = envelope.AuthoringPayload;
        var resolved = TryResolvePathValue(payload, definition.Path ?? definition.Name);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            return resolved;
        }

        if (!string.IsNullOrWhiteSpace(definition.Path)
            && definition.Path.StartsWith("=", StringComparison.Ordinal))
        {
            return definition.Path[1..];
        }

        return null;
    }

    private static bool TryResolveTopLevel(string? path, ProcessEventEnvelope envelope, out string? value)
    {
        var normalizedPath = path?.Trim();
        if (normalizedPath?.StartsWith("$.", StringComparison.Ordinal) == true)
        {
            normalizedPath = normalizedPath[2..];
        }

        value = normalizedPath?.ToLowerInvariant() switch
        {
            "id" or "eventid" => envelope.EventId,
            "type" or "eventtype" => envelope.EventType,
            "source" or "eventsource" => envelope.EventSource,
            "sequence" or "eventsequence" => envelope.EventSequence,
            _ => null
        };

        return !string.IsNullOrWhiteSpace(value);
    }

    private static string? ApplyNormalizer(string? rawValue, string? normalizer)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var normalized = rawValue;
        var operations = string.IsNullOrWhiteSpace(normalizer)
            ? []
            : normalizer.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var operation in operations)
        {
            normalized = operation.ToLowerInvariant() switch
            {
                "trim" => normalized.Trim(),
                "lower" or "lowercase" => normalized.ToLowerInvariant(),
                "upper" or "uppercase" => normalized.ToUpperInvariant(),
                _ => normalized
            };
        }

        return normalized;
    }

    private static string? TryResolvePathValue(JsonObject? parsedPayload, string? path)
    {
        if (parsedPayload is null || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var token = JsonPath.SelectToken(parsedPayload, path)
                    ?? JsonPath.SelectToken(parsedPayload, $"$.{path}");

        if (token is null)
        {
            return null;
        }

        return token switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            _ => token.ToJsonString().Trim('"')
        };
    }

    #endregion
}
