using System.Collections.Concurrent;
using Reimaginate.Orchestrator.Abstractions;
using YamlDotNet.RepresentationModel;

namespace Reimaginate.Orchestrator.Test.Base.Mocks;

public sealed class MockWorkflowDefinitionStore : IWorkflowDefinitionStore
{
    private const string WorkflowDefinitionSuffix = ".workflow.yaml";

    public IDictionary<string, string> Definitions { get; } = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IWorkflowDefinitionStore? FallbackStore { get; set; }

    public bool SupportsEventTriggerBindingQuery => true;

    public void Clear()
    {
        Definitions.Clear();
    }

    public void Set(string workflowType, string yamlDefinition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowType);
        ArgumentNullException.ThrowIfNull(yamlDefinition);

        Definitions[NormalizeWorkflowType(workflowType)] = yamlDefinition;
    }

    public bool TryGet(string workflowType, out string yamlDefinition)
    {
        yamlDefinition = string.Empty;
        if (string.IsNullOrWhiteSpace(workflowType))
        {
            return false;
        }

        if (Definitions.TryGetValue(workflowType, out var definition))
        {
            yamlDefinition = definition;
            return true;
        }

        if (Definitions.TryGetValue(NormalizeWorkflowType(workflowType), out definition))
        {
            yamlDefinition = definition;
            return true;
        }

        return false;
    }

    public Task<Result<string>> OpenReadAsync(string workflowType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (TryGet(workflowType, out var yamlDefinition))
        {
            return Task.FromResult(new Result<string>
            {
                Success = true,
                Data = yamlDefinition
            });
        }

        return FallbackStore?.OpenReadAsync(workflowType, cancellationToken)
            ?? Task.FromResult(new Result<string>
            {
                Success = false
            });
    }

    public Task<Result<bool>> ExistsAsync(string workflowType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (TryGet(workflowType, out _))
        {
            return Task.FromResult(new Result<bool>
            {
                Success = true,
                Data = true
            });
        }

        return FallbackStore?.ExistsAsync(workflowType, cancellationToken)
            ?? Task.FromResult(new Result<bool>
            {
                Success = true,
                Data = false
            });
    }

    public async IAsyncEnumerable<string> ListAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var workflowType in Definitions.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (seen.Add(workflowType))
            {
                yield return workflowType;
            }
        }

        if (FallbackStore is null)
        {
            yield break;
        }

        await foreach (var workflowType in FallbackStore.ListAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (seen.Add(workflowType))
            {
                yield return workflowType;
            }
        }
    }

    public async Task<IReadOnlyList<WorkflowEventTriggerBindingDescriptor>> FindBindingsByEventTypesAsync(IReadOnlyCollection<string> eventTypes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (eventTypes.Count == 0)
        {
            return [];
        }

        var requestedEventTypes = new HashSet<string>(eventTypes.Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
        if (requestedEventTypes.Count == 0)
        {
            return [];
        }

        var matches = new List<WorkflowEventTriggerBindingDescriptor>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in Definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bindings = ParseEventTriggerBindings(definition.Value, definition.Key);
            foreach (var binding in bindings.Where(x => requestedEventTypes.Contains(x.EventType)))
            {
                if (seen.Add(CreateBindingKey(binding)))
                {
                    matches.Add(binding);
                }
            }
        }

        if (FallbackStore is null)
        {
            return matches;
        }

        var fallbackBindings = await FallbackStore.FindBindingsByEventTypesAsync(eventTypes, cancellationToken);
        foreach (var binding in fallbackBindings)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (seen.Add(CreateBindingKey(binding)))
            {
                matches.Add(binding);
            }
        }

        return matches;
    }

    private static IReadOnlyList<WorkflowEventTriggerBindingDescriptor> ParseEventTriggerBindings(string yamlDefinition, string workflowType)
    {
        if (string.IsNullOrWhiteSpace(yamlDefinition))
        {
            return [];
        }

        var yaml = new YamlStream();
        using var reader = new StringReader(yamlDefinition);
        yaml.Load(reader);

        if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root)
        {
            return [];
        }

        return ParseEventTriggerBindings(root, workflowType);
    }

    private static readonly string[] LegacySupportedKeys = ["events", "listenEvents"];

    private static IReadOnlyList<WorkflowEventTriggerBindingDescriptor> ParseEventTriggerBindings(YamlMappingNode root, string workflowType)
    {
        var bindings = new List<WorkflowEventTriggerBindingDescriptor>();
        var hasCanonicalTriggers = false;

        if (TryGetChildMapping(root, "extensions", out var rootExtensions))
        {
            hasCanonicalTriggers = AddExtensionsEventBindings(rootExtensions, workflowType, bindings) || hasCanonicalTriggers;
        }

        if (TryGetChildMapping(root, "document", out var document)
            && TryGetChildMapping(document, "extensions", out var documentExtensions))
        {
            hasCanonicalTriggers = AddExtensionsEventBindings(documentExtensions, workflowType, bindings) || hasCanonicalTriggers;
        }

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

    private static void AddLegacyEventBindings(YamlMappingNode mapping, string workflowType, ICollection<WorkflowEventTriggerBindingDescriptor> bindings)
    {
        foreach (var key in LegacySupportedKeys)
        {
            if (!TryGetChildNode(mapping, key, out var node))
            {
                continue;
            }

            if (node is YamlScalarNode scalar && !string.IsNullOrWhiteSpace(scalar.Value))
            {
                bindings.Add(new WorkflowEventTriggerBindingDescriptor
                {
                    EventType = scalar.Value,
                    WorkflowType = workflowType,
                    Mode = "start"
                });
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
                    bindings.Add(new WorkflowEventTriggerBindingDescriptor
                    {
                        EventType = eventScalar.Value,
                        WorkflowType = workflowType,
                        Mode = "start"
                    });
                }
            }
        }
    }

    private static bool AddExtensionsEventBindings(YamlMappingNode extensions, string workflowType, ICollection<WorkflowEventTriggerBindingDescriptor> bindings)
    {
        if (!TryGetChildNode(extensions, "eventTriggers", out var eventTriggersNode))
        {
            return false;
        }

        if (eventTriggersNode is YamlScalarNode scalar && !string.IsNullOrWhiteSpace(scalar.Value))
        {
            bindings.Add(new WorkflowEventTriggerBindingDescriptor
            {
                EventType = scalar.Value,
                WorkflowType = workflowType,
                Mode = "start"
            });
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
                bindings.Add(new WorkflowEventTriggerBindingDescriptor
                {
                    EventType = triggerScalar.Value,
                    WorkflowType = workflowType,
                    Mode = "start"
                });
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

            bindings.Add(new WorkflowEventTriggerBindingDescriptor
            {
                EventType = eventType,
                WorkflowType = workflowType,
                Filter = TryGetScalarValue(triggerMapping, "filter") ?? TryGetMatchWhen(triggerMapping),
                EventTypePath = TryGetScalarValue(triggerMapping, "eventTypePath"),
                Mode = TryGetScalarValue(triggerMapping, "mode")
            });
        }
        return true;
    }

    private static string? TryGetScalarValue(YamlMappingNode mapping, string key)
    {
        if (!TryGetChildNode(mapping, key, out var node) || node is not YamlScalarNode scalar)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(scalar.Value) ? null : scalar.Value;
    }

    private static string? TryGetMatchWhen(YamlMappingNode mapping)
    {
        if (!TryGetChildNode(mapping, "match", out var matchNode) || matchNode is not YamlMappingNode matchMapping)
        {
            return null;
        }

        return TryGetScalarValue(matchMapping, "when");
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

    private static bool TryGetChildNode(YamlMappingNode mapping, string key, out YamlNode child)
    {
        foreach (var entry in mapping.Children)
        {
            if (entry.Key is YamlScalarNode scalar
                && string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase))
            {
                child = entry.Value;
                return true;
            }
        }

        child = null!;
        return false;
    }

    private static string CreateBindingKey(WorkflowEventTriggerBindingDescriptor binding)
        => $"{binding.EventType}|{binding.WorkflowType}|{binding.Filter}|{binding.EventTypePath}|{binding.Mode}";

    public static string NormalizeWorkflowType(string workflowType)
        => workflowType.EndsWith(WorkflowDefinitionSuffix, StringComparison.OrdinalIgnoreCase)
            ? workflowType
            : $"{workflowType}{WorkflowDefinitionSuffix}";
}
