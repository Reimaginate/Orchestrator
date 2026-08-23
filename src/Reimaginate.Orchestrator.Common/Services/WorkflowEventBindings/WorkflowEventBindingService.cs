using System.Text.Json.Nodes;
using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.Common.Config;
using Reimaginate.Orchestrator.Common.Constants;
using Reimaginate.Orchestrator.Common.Models;
using Reimaginate.Orchestrator.Common.Stores.WorkflowDefinitions;
using YamlDotNet.RepresentationModel;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowEventBindings;

public sealed class WorkflowEventBindingService : IWorkflowEventBindingService
{
    private readonly IWorkflowDefinitionStore? workflowDefinitionStore;

    public WorkflowEventBindingService(
        IWorkflowDefinitionStore? workflowDefinitionStore = null)
    {
        this.workflowDefinitionStore = workflowDefinitionStore;
    }

    #region Constants

    private static readonly string[] LegacySupportedKeys = ["events", "listenEvents"];

    #endregion

    #region Public API

    public IReadOnlyList<WorkflowEventBinding> ResolveListeningWorkflowEventBindings(
        IReadOnlyDictionary<string, IReadOnlyList<WorkflowEventBinding>> workflowEventMap,
        IEnumerable<string> eventTypes)
    {
        // Merge bindings from all requested event types while preventing duplicate workflow/event/mode combinations.
        var bindings = new List<WorkflowEventBinding>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var eventType in eventTypes)
        {
            if (string.IsNullOrWhiteSpace(eventType) || !workflowEventMap.TryGetValue(eventType, out var eventBindings))
            {
                continue;
            }

            foreach (var binding in eventBindings)
            {
                if (!seen.Add(BuildBindingDeduplicationKey(binding)))
                {
                    continue;
                }

                bindings.Add(binding);
            }
        }

        return bindings;
    }

    public async Task<IReadOnlyList<WorkflowEventBinding>> ResolveListeningWorkflowEventBindingsAsync(
        IEnumerable<string> eventTypes,
        CancellationToken cancellationToken)
    {
        var requestedEventTypes = eventTypes
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (requestedEventTypes.Length == 0)
        {
            return [];
        }

        return await ResolveListeningWorkflowEventBindingsUncachedAsync(requestedEventTypes, cancellationToken);
    }

    public IReadOnlyDictionary<string, IReadOnlyList<WorkflowEventBinding>> ResolveWorkflowEventMap()
    {
        if (workflowDefinitionStore is not null)
        {
            return ResolveWorkflowEventMap(workflowDefinitionStore);
        }

        // Default convention: workflow definitions are loaded from the runtime SystemWorkflows folder.
        var workflowsDirectory = Path.Combine(AppContext.BaseDirectory, WorkflowDefinitionStorageDefaults.DefaultFileSystemFolderName);
        return ResolveWorkflowEventMap(workflowsDirectory);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<WorkflowEventBinding>> ResolveWorkflowEventMap(IWorkflowDefinitionStore workflowDefinitionStore)
    {
        var map = new Dictionary<string, List<WorkflowEventBinding>>(StringComparer.OrdinalIgnoreCase);
        foreach (var workflowType in ListWorkflowTypes(workflowDefinitionStore))
        {
            var workflowResponse = workflowDefinitionStore.OpenReadAsync(workflowType, CancellationToken.None).GetAwaiter().GetResult();
            if (!workflowResponse.Success || string.IsNullOrWhiteSpace(workflowResponse.Data))
            {
                continue;
            }

            var eventBindings = ExtractEventBindings(workflowResponse.Data, workflowType);

            foreach (var binding in eventBindings)
            {
                if (!map.TryGetValue(binding.EventType, out var workflows))
                {
                    workflows = [];
                    map[binding.EventType] = workflows;
                }

                workflows.Add(ToWorkflowEventBinding(binding));
            }
        }

        return map.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<WorkflowEventBinding>)kvp.Value,
            StringComparer.OrdinalIgnoreCase);
    }


    private static IEnumerable<string> ListWorkflowTypes(IWorkflowDefinitionStore workflowDefinitionStore)
    {
        var enumerator = workflowDefinitionStore.ListAsync(CancellationToken.None).GetAsyncEnumerator(CancellationToken.None);

        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                yield return enumerator.Current;
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public IReadOnlyDictionary<string, IReadOnlyList<WorkflowEventBinding>> ResolveWorkflowEventMap(string workflowsDirectory)
    {
        // Return an empty map when the configured workflow directory is absent.
        if (!Directory.Exists(workflowsDirectory))
        {
            return new Dictionary<string, IReadOnlyList<WorkflowEventBinding>>(StringComparer.OrdinalIgnoreCase);
        }

        var map = new Dictionary<string, List<WorkflowEventBinding>>(StringComparer.OrdinalIgnoreCase);
        var workflowFiles = Directory
            .GetFiles(workflowsDirectory, "*.workflow.yaml", SearchOption.AllDirectories)
            .OrderBy(path => FileSystemWorkflowDefinitionStore.ToCanonicalWorkflowType(workflowsDirectory, path), StringComparer.OrdinalIgnoreCase);

        foreach (var workflowPath in workflowFiles)
        {
            var workflowType = FileSystemWorkflowDefinitionStore.ToCanonicalWorkflowType(workflowsDirectory, workflowPath);
            var eventBindings = ExtractEventBindingsFromPath(workflowPath, workflowType);

            foreach (var binding in eventBindings)
            {
                if (!map.TryGetValue(binding.EventType, out var workflows))
                {
                    workflows = [];
                    map[binding.EventType] = workflows;
                }

                workflows.Add(ToWorkflowEventBinding(binding));
            }
        }

        return map.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<WorkflowEventBinding>)kvp.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    #endregion

    #region Workflow file parsing

    private static IEnumerable<ResolvedWorkflowBinding> ExtractEventBindingsFromPath(string workflowPath, string workflowType)
    {
        using var reader = new StreamReader(workflowPath);
        return ExtractEventBindings(reader, workflowType);
    }

    private static IEnumerable<ResolvedWorkflowBinding> ExtractEventBindings(string workflowYaml, string workflowType)
    {
        using var reader = new StringReader(workflowYaml);
        return ExtractEventBindings(reader, workflowType);
    }

    private static IEnumerable<ResolvedWorkflowBinding> ExtractEventBindings(TextReader reader, string workflowType)
    {
        var yaml = new YamlStream();
        yaml.Load(reader);

        if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root)
        {
            return [];
        }

        var bindings = new List<ResolvedWorkflowBinding>();
        var hasCanonicalTriggers = false;

        // Canonical triggers are defined under document.extensions.eventTriggers.
        if (TryGetChildMapping(root, "document", out var document)
            && TryGetChildMapping(document, "extensions", out var extensions))
        {
            hasCanonicalTriggers = AddExtensionsEventBindings(extensions, workflowType, bindings) || hasCanonicalTriggers;
        }

        // Some workflows place extensions at the root level.
        if (TryGetChildMapping(root, "extensions", out var rootExtensions))
        {
            hasCanonicalTriggers = AddExtensionsEventBindings(rootExtensions, workflowType, bindings) || hasCanonicalTriggers;
        }

        // Fallback to legacy keys only when canonical triggers are not declared.
        if (!hasCanonicalTriggers)
        {
            AddLegacyEventBindings(root, workflowType, bindings);

            if (TryGetChildMapping(root, "document", out var legacyDocument))
            {
                AddLegacyEventBindings(legacyDocument, workflowType, bindings);
            }
        }

        return bindings;
    }

    #endregion

    #region Binding extraction strategies

    private static void AddLegacyEventBindings(YamlMappingNode mapping, string workflowType, ICollection<ResolvedWorkflowBinding> bindings)
    {
        // Legacy schema supports either a scalar event name or a sequence under events/listenEvents.
        foreach (var key in LegacySupportedKeys)
        {
            if (!TryGetChildNode(mapping, key, out var node))
            {
                continue;
            }

            if (node is YamlScalarNode scalar && !string.IsNullOrWhiteSpace(scalar.Value))
            {
                bindings.Add(new ResolvedWorkflowBinding(scalar.Value, workflowType, null, null, null, WorkflowEventBindingMode.Start, null));
                continue;
            }

            if (node is not YamlSequenceNode sequence)
            {
                continue;
            }

            foreach (var eventNode in sequence.Children)
            {
                if (eventNode is YamlScalarNode eventScalar && !string.IsNullOrWhiteSpace(eventScalar.Value))
                {
                    bindings.Add(new ResolvedWorkflowBinding(eventScalar.Value, workflowType, null, null, null, WorkflowEventBindingMode.Start, null));
                }
            }
        }
    }

    private static bool AddExtensionsEventBindings(YamlMappingNode extensions, string workflowType, ICollection<ResolvedWorkflowBinding> bindings)
    {
        if (!TryGetChildNode(extensions, "eventTriggers", out var eventTriggersNode))
        {
            return false;
        }

        // Single scalar trigger is treated as a Start binding.
        if (eventTriggersNode is YamlScalarNode scalar && !string.IsNullOrWhiteSpace(scalar.Value))
        {
            bindings.Add(new ResolvedWorkflowBinding(scalar.Value, workflowType, null, null, null, WorkflowEventBindingMode.Start, null));
            return true;
        }

        if (eventTriggersNode is not YamlSequenceNode sequence)
        {
            return true;
        }

        foreach (var triggerNode in sequence.Children)
        {
            if (triggerNode is YamlScalarNode triggerScalar && !string.IsNullOrWhiteSpace(triggerScalar.Value))
            {
                bindings.Add(new ResolvedWorkflowBinding(triggerScalar.Value, workflowType, null, null, null, WorkflowEventBindingMode.Start, null));
                continue;
            }

            if (triggerNode is not YamlMappingNode triggerMapping)
            {
                continue;
            }

            var eventType = TryGetScalarValue(triggerMapping, "event")
                            ?? TryGetScalarValue(triggerMapping, "eventType")
                            ?? TryGetScalarValue(triggerMapping, "name")
                            ?? TryGetScalarValue(triggerMapping, "type");

            if (string.IsNullOrWhiteSpace(eventType))
            {
                continue;
            }

            // Mapping triggers can define filter, mode, input template and correlation metadata.
            var filter = TryGetScalarValue(triggerMapping, "filter");
            var eventTypePath = TryGetScalarValue(triggerMapping, "eventTypePath");
            var inputTemplate = TryGetJsonObject(triggerMapping, "eventInput");
            var mode = TryParseMode(TryGetScalarValue(triggerMapping, "mode"));
            var correlation = TryGetCorrelation(triggerMapping, "correlation");

            // Optional shorthand syntax under match: { when, by }.
            if (TryGetMatch(triggerMapping, out var shorthandFilter, out var shorthandCorrelation))
            {
                filter ??= shorthandFilter;
                correlation ??= shorthandCorrelation;
            }

            bindings.Add(new ResolvedWorkflowBinding(eventType, workflowType, inputTemplate, filter, eventTypePath, mode, correlation));
        }

        return true;
    }

    #endregion

    #region Binding field normalization

    private static WorkflowEventBindingMode TryParseMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return WorkflowEventBindingMode.Start;
        }

        return mode.Trim().ToLowerInvariant() switch
        {
            "start" => WorkflowEventBindingMode.Start,
            "resume" => WorkflowEventBindingMode.Resume,
            "startorresume" or "start_or_resume" or "start-or-resume" => WorkflowEventBindingMode.StartOrResume,
            _ => WorkflowEventBindingMode.Start
        };
    }

    private static WorkflowEventCorrelationBinding? TryGetCorrelation(YamlMappingNode mapping, string key)
    {
        if (!TryGetChildNode(mapping, key, out var node) || node is not YamlMappingNode correlation)
        {
            return null;
        }

        var keyDefinitions = new List<WorkflowCorrelationKeyDefinition>();

        if (TryGetChildNode(correlation, "keys", out var keysNode) && keysNode is YamlSequenceNode keysSequence)
        {
            foreach (var keyNode in keysSequence.Children.OfType<YamlMappingNode>())
            {
                var name = TryGetScalarValue(keyNode, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                keyDefinitions.Add(new WorkflowCorrelationKeyDefinition
                {
                    Name = name,
                    Path = TryGetScalarValue(keyNode, "path"),
                    Required = bool.TryParse(TryGetScalarValue(keyNode, "required"), out var required) && required,
                    Normalizer = TryGetScalarValue(keyNode, "normalizer")
                });
            }
        }

        if (keyDefinitions.Count == 0)
        {
            // Backward-compatible fallback for older correlation shape.
            var entityIdPath = TryGetScalarValue(correlation, "entityIdPath");
            var entityType = TryGetScalarValue(correlation, "entityType");
            var tenantIdPath = TryGetScalarValue(correlation, "tenantIdPath");
            var workspaceIdPath = TryGetScalarValue(correlation, "workspaceIdPath");

            if (!string.IsNullOrWhiteSpace(entityType))
            {
                keyDefinitions.Add(new WorkflowCorrelationKeyDefinition { Name = "entityType", Path = $"={entityType}", Normalizer = "trim|lower" });
            }

            if (!string.IsNullOrWhiteSpace(entityIdPath))
            {
                keyDefinitions.Add(new WorkflowCorrelationKeyDefinition { Name = "entityId", Path = entityIdPath, Required = true, Normalizer = "trim|lower" });
            }

            if (!string.IsNullOrWhiteSpace(tenantIdPath))
            {
                keyDefinitions.Add(new WorkflowCorrelationKeyDefinition { Name = "tenantId", Path = tenantIdPath, Normalizer = "trim|lower" });
            }

            if (!string.IsNullOrWhiteSpace(workspaceIdPath))
            {
                keyDefinitions.Add(new WorkflowCorrelationKeyDefinition { Name = "workspaceId", Path = workspaceIdPath, Normalizer = "trim|lower" });
            }
        }

        return keyDefinitions.Count == 0
            ? null
            : new WorkflowEventCorrelationBinding { Keys = keyDefinitions };
    }

    private static bool TryGetMatch(
        YamlMappingNode mapping,
        out string? filter,
        out WorkflowEventCorrelationBinding? correlation)
    {
        filter = null;
        correlation = null;

        if (!TryGetChildNode(mapping, "match", out var matchNode) || matchNode is not YamlMappingNode matchMapping)
        {
            return false;
        }

        filter = TryGetScalarValue(matchMapping, "when");
        correlation = TryGetMatchCorrelation(matchMapping);
        return true;
    }

    private static WorkflowEventCorrelationBinding? TryGetMatchCorrelation(YamlMappingNode matchMapping)
    {
        if (!TryGetChildNode(matchMapping, "by", out var byNode) || byNode is not YamlMappingNode byMapping)
        {
            return null;
        }

        var keyDefinitions = new List<WorkflowCorrelationKeyDefinition>();

        foreach (var entry in byMapping.Children)
        {
            if (entry.Key is not YamlScalarNode keyNode || string.IsNullOrWhiteSpace(keyNode.Value))
            {
                continue;
            }

            var rawName = keyNode.Value.Trim();
            var required = rawName.EndsWith('!');
            var name = required ? rawName[..^1] : rawName;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (entry.Value is YamlScalarNode scalarValue && !string.IsNullOrWhiteSpace(scalarValue.Value))
            {
                ParseCorrelationPathAndNormalizer(scalarValue.Value, out var path, out var normalizer);
                keyDefinitions.Add(new WorkflowCorrelationKeyDefinition
                {
                    Name = name,
                    Path = path,
                    Required = required,
                    Normalizer = normalizer
                });

                continue;
            }

            if (entry.Value is YamlMappingNode valueMapping)
            {
                keyDefinitions.Add(new WorkflowCorrelationKeyDefinition
                {
                    Name = name,
                    Path = TryGetScalarValue(valueMapping, "path"),
                    Required = required || (bool.TryParse(TryGetScalarValue(valueMapping, "required"), out var isRequired) && isRequired),
                    Normalizer = TryGetScalarValue(valueMapping, "normalizer")
                });
            }
        }

        return keyDefinitions.Count == 0
            ? null
            : new WorkflowEventCorrelationBinding { Keys = keyDefinitions };
    }

    private static void ParseCorrelationPathAndNormalizer(string rawExpression, out string? path, out string? normalizer)
    {
        path = null;
        normalizer = null;

        if (string.IsNullOrWhiteSpace(rawExpression))
        {
            return;
        }

        var segments = rawExpression
            .Split('|', StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();

        if (segments.Length == 0)
        {
            return;
        }

        path = segments[0];
        normalizer = segments.Length > 1 ? string.Join('|', segments.Skip(1)) : null;
    }

    #endregion

    #region YAML to JSON conversion helpers

    private static JsonObject? TryGetJsonObject(YamlMappingNode mapping, string key)
    {
        if (!TryGetChildNode(mapping, key, out var node) || node is not YamlMappingNode inputMapping)
        {
            return null;
        }

        var json = ConvertYamlNode(inputMapping);
        return json as JsonObject;
    }

    private static JsonNode? ConvertYamlNode(YamlNode node)
    {
        return node switch
        {
            YamlScalarNode scalar => ConvertScalar(scalar),
            YamlSequenceNode sequence => new JsonArray(sequence.Children.Select(ConvertYamlNode).ToArray()),
            YamlMappingNode mapping => new JsonObject(mapping.Children
                .OfType<KeyValuePair<YamlNode, YamlNode>>()
                .Where(kvp => kvp.Key is YamlScalarNode)
                .ToDictionary(kvp => ((YamlScalarNode)kvp.Key).Value ?? string.Empty, kvp => ConvertYamlNode(kvp.Value))),
            _ => null
        };
    }

    private static JsonNode? ConvertScalar(YamlScalarNode scalar)
    {
        if (scalar.Value is null)
        {
            return null;
        }

        if (bool.TryParse(scalar.Value, out var boolValue))
        {
            return JsonValue.Create(boolValue);
        }

        if (long.TryParse(scalar.Value, out var longValue))
        {
            return JsonValue.Create(longValue);
        }

        if (decimal.TryParse(scalar.Value, out var decimalValue))
        {
            return JsonValue.Create(decimalValue);
        }

        return JsonValue.Create(scalar.Value);
    }

    #endregion

    #region YAML navigation helpers

    private static string? TryGetScalarValue(YamlMappingNode mapping, string key)
    {
        if (!TryGetChildNode(mapping, key, out var node) || node is not YamlScalarNode scalar)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(scalar.Value) ? null : scalar.Value;
    }

    private static bool TryGetChildMapping(YamlMappingNode mapping, string key, out YamlMappingNode child)
    {
        child = null!;
        if (!TryGetChildNode(mapping, key, out var node) || node is not YamlMappingNode mappingNode)
        {
            return false;
        }

        child = mappingNode;
        return true;
    }

    private static bool TryGetChildNode(YamlMappingNode mapping, string key, out YamlNode node)
    {
        foreach (var kvp in mapping.Children)
        {
            if (kvp.Key is not YamlScalarNode scalar || !string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            node = kvp.Value;
            return true;
        }

        node = null!;
        return false;
    }


    private static WorkflowEventBinding ToWorkflowEventBinding(ResolvedWorkflowBinding binding)
    {
        return new WorkflowEventBinding
        {
            EventType = binding.EventType,
            WorkflowType = binding.WorkflowType,
            InputTemplate = binding.InputTemplate,
            Filter = binding.Filter,
            EventTypePath = binding.EventTypePath,
            Mode = binding.Mode,
            Correlation = binding.Correlation
        };
    }

    private static string BuildBindingDeduplicationKey(WorkflowEventBinding binding)
        => $"{binding.WorkflowType}|{binding.EventType}|{binding.Mode}|{binding.EventTypePath}|{binding.Filter}";

    private static string BuildBindingDescriptorKey(WorkflowEventBinding binding)
        => $"{binding.EventType}|{binding.Mode}|{binding.EventTypePath}|{binding.Filter}";

    private static string BuildDescriptorKey(WorkflowEventTriggerBindingDescriptor descriptor)
    {
        var mode = TryParseMode(descriptor.Mode);
        return $"{descriptor.EventType}|{mode}|{descriptor.EventTypePath}|{descriptor.Filter}";
    }

    private async Task<IReadOnlyList<WorkflowEventBinding>> ResolveListeningWorkflowEventBindingsUncachedAsync(
        IReadOnlyList<string> requestedEventTypes,
        CancellationToken cancellationToken)
    {
        if (workflowDefinitionStore is null || !workflowDefinitionStore.SupportsEventTriggerBindingQuery)
        {
            var workflowEventMap = ResolveWorkflowEventMap();
            return ResolveListeningWorkflowEventBindings(workflowEventMap, requestedEventTypes);
        }

        var descriptors = await workflowDefinitionStore.FindBindingsByEventTypesAsync(requestedEventTypes, cancellationToken);
        if (descriptors.Count == 0)
        {
            return [];
        }

        var descriptorLookup = descriptors
            .Where(x => !string.IsNullOrWhiteSpace(x.EventType) && !string.IsNullOrWhiteSpace(x.WorkflowType))
            .GroupBy(x => x.WorkflowType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new HashSet<string>(group.Select(BuildDescriptorKey), StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        var resolvedBindings = new List<WorkflowEventBinding>();

        foreach (var workflowType in descriptorLookup.Keys)
        {
            var workflowResponse = await workflowDefinitionStore.OpenReadAsync(workflowType, cancellationToken);
            if (!workflowResponse.Success || string.IsNullOrWhiteSpace(workflowResponse.Data))
            {
                continue;
            }

            var workflowBindings = ExtractEventBindings(workflowResponse.Data, workflowType)
                .Select(ToWorkflowEventBinding)
                .Where(binding => requestedEventTypes.Contains(binding.EventType, StringComparer.OrdinalIgnoreCase))
                .Where(binding => descriptorLookup[workflowType].Contains(BuildBindingDescriptorKey(binding)))
                .ToArray();

            resolvedBindings.AddRange(workflowBindings);
        }

        return resolvedBindings
            .GroupBy(BuildBindingDeduplicationKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    #endregion

    #region Internal models

    private sealed record ResolvedWorkflowBinding(
        string EventType,
        string WorkflowType,
        JsonObject? InputTemplate,
        string? Filter,
        string? EventTypePath,
        WorkflowEventBindingMode Mode,
        WorkflowEventCorrelationBinding? Correlation);

    #endregion
}
