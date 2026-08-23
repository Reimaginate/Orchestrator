using System.Collections;
using System.Reflection;
using System.Text.Json.Nodes;
using Reimaginate.Orchestrator.DSL;

namespace Reimaginate.Orchestrator.Common.Requests.Internal.BuildAgentWorkflow;

internal static class SwitchTaskAdapter
{
    public const string EndDirective = "end";
    public const string NullDirective = "null";

    public static bool IsEndDirective(object? thenValue)
    {
        return thenValue switch
        {
            string text => IsEndDirectiveText(text),
            JsonValue value when value.TryGetValue<string>(out var text) => IsEndDirectiveText(text),
            _ => false
        };
    }

    public static bool IsNullDirective(object? thenValue)
    {
        return thenValue switch
        {
            string text => IsNullDirectiveText(text),
            JsonValue value when value.TryGetValue<string>(out var text) => IsNullDirectiveText(text),
            _ => false
        };
    }

    public static IReadOnlyList<WorkflowSwitchBranch> ReadBranches(SwitchTaskDefinition switchTaskDefinition, string locationHint)
    {
        ArgumentNullException.ThrowIfNull(switchTaskDefinition);

        var branches = new List<WorkflowSwitchBranch>();
        var index = 0;

        foreach (var branch in switchTaskDefinition.Switch)
        {
            var branchLocation = $"{locationHint}.switch[{index}]";
            var isDefault = string.Equals(branch.Key, "default", StringComparison.OrdinalIgnoreCase);
            var when = NormalizeWhen(branch.Value.When);
            var output = branch.Value.Output;
            object? then = branch.Value.Then;

            branches.Add(new WorkflowSwitchBranch(when, output, then, isDefault, branchLocation));
            index++;
        }

        if (branches.Count > 0)
        {
            return branches;
        }

        return TryReadBranchesViaReflection(switchTaskDefinition, locationHint, out var reflectedBranches)
            ? reflectedBranches
            : [];
    }

    private static bool TryReadBranchesViaReflection(
        SwitchTaskDefinition switchTaskDefinition,
        string locationHint,
        out IReadOnlyList<WorkflowSwitchBranch> branches)
    {
        var switchProperty = switchTaskDefinition.GetType().GetProperty("Switch", BindingFlags.Public | BindingFlags.Instance);
        var switchValue = switchProperty?.GetValue(switchTaskDefinition);

        if (switchValue is JsonArray switchCases)
        {
            branches = ReadBranchesFromJsonArray(switchCases, locationHint);
            return true;
        }

        if (switchValue is IEnumerable sequence and not string)
        {
            branches = ReadBranchesFromUntypedEntries(sequence, locationHint);
            return branches.Count > 0;
        }

        branches = [];
        return false;
    }

    private static IReadOnlyList<WorkflowSwitchBranch> ReadBranchesFromUntypedEntries(IEnumerable entries, string locationHint)
    {
        var branches = new List<WorkflowSwitchBranch>();
        var index = 0;

        foreach (var entry in entries)
        {
            var branchLocation = $"{locationHint}.switch[{index}]";
            if (entry is null)
            {
                branches.Add(new WorkflowSwitchBranch(null, null, null, false, branchLocation));
                index++;
                continue;
            }

            var entryType = entry.GetType();
            var key = entryType.GetProperty("Key")?.GetValue(entry)?.ToString();
            var value = entryType.GetProperty("Value")?.GetValue(entry);

            var isDefault = string.Equals(key, "default", StringComparison.OrdinalIgnoreCase);
            var when = value?.GetType().GetProperty("When")?.GetValue(value)?.ToString();
            var output = value?.GetType().GetProperty("Output")?.GetValue(value);
            var then = value?.GetType().GetProperty("Then")?.GetValue(value);

            branches.Add(new WorkflowSwitchBranch(NormalizeWhen(when), output as TaskOutputDefinition, then, isDefault, branchLocation));
            index++;
        }

        return branches;
    }

    private static IReadOnlyList<WorkflowSwitchBranch> ReadBranchesFromJsonArray(JsonArray switchCases, string locationHint)
    {
        var branches = new List<WorkflowSwitchBranch>();

        for (var index = 0; index < switchCases.Count; index++)
        {
            var branchLocation = $"{locationHint}.switch[{index}]";
            if (switchCases[index] is not JsonObject branch)
            {
                branches.Add(new WorkflowSwitchBranch(null, null, null, false, branchLocation));
                continue;
            }

            var whenNode = branch["when"] ?? branch["When"];
            var thenNode = branch["then"] ?? branch["Then"];
            var outputNode = branch["output"] ?? branch["Output"];
            var defaultNode = branch["default"] ?? branch["Default"];
            var output = outputNode is JsonObject outputObject
                ? ReadOutputDefinition(outputObject)
                : null;

            if (defaultNode is not null)
            {
                branches.Add(new WorkflowSwitchBranch(NodeToString(whenNode), output, defaultNode, true, branchLocation));
                continue;
            }

            branches.Add(new WorkflowSwitchBranch(NodeToString(whenNode), output, thenNode, false, branchLocation));
        }

        return branches;
    }

    private static TaskOutputDefinition? ReadOutputDefinition(JsonObject outputObject)
    {
        var asNode = outputObject["as"] ?? outputObject["As"];
        var stashNode = outputObject["stash"] ?? outputObject["Stash"];

        if (asNode is null && stashNode is null)
        {
            return null;
        }

        return new TaskOutputDefinition
        {
            As = asNode,
            Stash = stashNode is JsonObject stashObject
                ? stashObject.ToDictionary(entry => entry.Key, entry => (object?)entry.Value ?? null!, StringComparer.Ordinal)
                : null
        };
    }

    private static string? NodeToString(JsonNode? node)
    {
        return node switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            _ => node.ToJsonString()
        };
    }

    private static string? NormalizeWhen(string? when)
    {
        return string.IsNullOrWhiteSpace(when) ? when : when.Trim();
    }

    private static bool IsEndDirectiveText(string text)
        => string.Equals(text.Trim(), EndDirective, StringComparison.OrdinalIgnoreCase);

    private static bool IsNullDirectiveText(string text)
        => string.Equals(text.Trim(), NullDirective, StringComparison.OrdinalIgnoreCase);
}
