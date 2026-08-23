using Reimaginate.Orchestrator.Abstractions;
using YamlDotNet.RepresentationModel;

namespace Reimaginate.Orchestrator.Common.Stores.WorkflowDefinitions.Parsing;

public static class WorkflowEventTriggerBindingDescriptorParser
{
    private static readonly string[] LegacySupportedKeys = ["events", "listenEvents"];

    public static IReadOnlyList<WorkflowEventTriggerBindingDescriptor> Parse(Stream workflowStream, string workflowType)
    {
        using var reader = new StreamReader(workflowStream, leaveOpen: true);

        var yaml = new YamlStream();
        yaml.Load(reader);

        if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root)
        {
            return [];
        }

        return Parse(root, workflowType);
    }

    public static IReadOnlyList<WorkflowEventTriggerBindingDescriptor> Parse(YamlMappingNode root, string workflowType)
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
}
