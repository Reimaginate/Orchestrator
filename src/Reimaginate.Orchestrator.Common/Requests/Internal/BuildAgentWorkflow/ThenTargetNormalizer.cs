using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reimaginate.Orchestrator.Common.Requests.Internal.BuildAgentWorkflow;

internal abstract record ThenTargetDescriptor;

internal sealed record NamedTaskTarget(string Name) : ThenTargetDescriptor;

internal sealed record InlineTaskTarget(string InternalName, JsonObject InlineDefinition, string LocationHint) : ThenTargetDescriptor;

internal sealed class ThenTargetNormalizer(Func<string, string, string> internalNameFactory)
{
    public IEnumerable<ThenTargetDescriptor> Normalize(object? thenValue, string parentTaskName, string locationHint)
    {
        return NormalizeCore(thenValue, parentTaskName, locationHint);
    }

    private IEnumerable<ThenTargetDescriptor> NormalizeCore(object? thenValue, string parentTaskName, string locationHint)
    {
        switch (thenValue)
        {
            case null:
                yield break;
            case string nextTaskName when !string.IsNullOrWhiteSpace(nextTaskName):
                yield return new NamedTaskTarget(nextTaskName);
                yield break;
            case JsonValue jsonValue when jsonValue.TryGetValue<string>(out var jsonTaskName) && !string.IsNullOrWhiteSpace(jsonTaskName):
                yield return new NamedTaskTarget(jsonTaskName);
                yield break;
            case JsonArray jsonArray:
                for (var index = 0; index < jsonArray.Count; index++)
                {
                    foreach (var target in NormalizeCore(jsonArray[index], parentTaskName, $"{locationHint}.then[{index}]"))
                    {
                        yield return target;
                    }
                }

                yield break;
            case JsonObject jsonObject:
                yield return NormalizeInlineTarget(jsonObject, parentTaskName, locationHint);
                yield break;
            case IEnumerable sequence:
                var sequenceIndex = 0;
                foreach (var item in sequence)
                {
                    foreach (var target in NormalizeCore(item, parentTaskName, $"{locationHint}.then[{sequenceIndex}]"))
                    {
                        yield return target;
                    }

                    sequenceIndex++;
                }

                yield break;
            default:
                foreach (var target in NormalizeSerializedNode(thenValue, parentTaskName, locationHint))
                {
                    yield return target;
                }

                yield break;
        }
    }

    private IEnumerable<ThenTargetDescriptor> NormalizeSerializedNode(object thenValue, string parentTaskName, string locationHint)
    {
        var serializedNode = JsonSerializer.SerializeToNode(thenValue);
        if (ReferenceEquals(serializedNode, thenValue))
        {
            throw new InvalidOperationException($"Unsupported 'then' value type '{thenValue.GetType().Name}' for task '{parentTaskName}' at {locationHint}.");
        }

        foreach (var target in NormalizeCore(serializedNode, parentTaskName, locationHint))
        {
            yield return target;
        }
    }

    private InlineTaskTarget NormalizeInlineTarget(JsonObject inlineTaskNode, string parentTaskName, string locationHint)
    {
        if (inlineTaskNode.Count != 1)
        {
            throw new InvalidOperationException($"Inline task block for '{parentTaskName}' at {locationHint} must contain exactly one named task.");
        }

        var inlineTask = inlineTaskNode.First();
        var inlineTaskName = inlineTask.Key;
        var inlineTaskDefinition = inlineTask.Value as JsonObject
            ?? throw new InvalidOperationException($"Inline task '{inlineTaskName}' at {locationHint} is not an object.");

        var internalName = internalNameFactory(parentTaskName, inlineTaskName);
        var inlineLocationHint = $"{locationHint}.{inlineTaskName}({internalName})";

        return new InlineTaskTarget(internalName, inlineTaskDefinition, inlineLocationHint);
    }
}
