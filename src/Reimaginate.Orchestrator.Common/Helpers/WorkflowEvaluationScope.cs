using System.Text.Json.Nodes;

namespace Reimaginate.Orchestrator.Common.Helpers;

/// <summary>
/// Provides an immutable, read-only view over expression inputs without constructing
/// a merged JSON object for environment, fallback, or predicate-item overlays.
/// </summary>
internal readonly record struct WorkflowEvaluationScope(
    JsonNode Root,
    JsonObject? Environment = null,
    JsonNode? Fallback = null,
    JsonNode? Item = null,
    bool HasItem = false,
    WorkflowRuntimeEvaluationOverlay? Runtime = null,
    JsonObject? StashOverlay = null)
{
    public WorkflowEvaluationScope WithItem(JsonNode? item)
        => this with { Item = item, HasItem = true };

    public object? Resolve(string path, string expression)
        => Resolve(JsonPath.Compile(path), expression);

    public object? Resolve(JsonPath.CompiledPath path, string expression)
    {
        using var tokens = SelectTokens(path).GetEnumerator();
        return tokens.MoveNext()
            ? tokens.Current
            : new WorkflowMissingValue(path.Source, expression);
    }

    public JsonNode? SelectToken(string path)
        => SelectTokens(JsonPath.Compile(path)).FirstOrDefault();

    public IEnumerable<JsonNode?> SelectTokens(string path)
        => SelectTokens(JsonPath.Compile(path));

    public IEnumerable<JsonNode?> SelectTokens(JsonPath.CompiledPath path)
    {
        if (Environment is not null && TryReadRootProperty(path.Source, "env", out var environmentRemainder))
        {
            foreach (var token in SelectOverlay(Environment, environmentRemainder))
            {
                yield return token;
            }

            yield break;
        }

        if (HasItem && TryReadRootProperty(path.Source, "item", out var itemRemainder))
        {
            foreach (var token in SelectItem(Item, itemRemainder))
            {
                yield return token;
            }

            yield break;
        }

        if (StashOverlay is not null
            && TryReadRootProperty(path.Source, "stash", out _))
        {
            foreach (var token in SelectStashOverlay(path))
            {
                yield return token;
            }

            yield break;
        }

        if (Runtime is not null
            && TrySelectRuntime(path, Runtime, out var runtimeTokens))
        {
            foreach (var token in runtimeTokens)
            {
                yield return token;
            }

            yield break;
        }

        if (Fallback is not null)
        {
            foreach (var token in SelectDeepOverlay(Root, Fallback, path))
            {
                yield return token;
            }

            yield break;
        }

        foreach (var token in path.SelectTokens(Root))
        {
            yield return token;
        }
    }

    private IEnumerable<JsonNode?> SelectStashOverlay(JsonPath.CompiledPath path)
    {
        if (path.Steps.Count == 1)
        {
            yield return MaterializeStash();
            yield break;
        }

        var nextStep = path.Steps[1];
        if (nextStep.Kind == JsonPath.StepKind.Property
            && StashOverlay!.TryGetPropertyValue(nextStep.Name!, out var overlaidValue))
        {
            foreach (var token in ApplySteps(overlaidValue, path.Steps, 2))
            {
                yield return token;
            }

            yield break;
        }

        if (nextStep.Kind == JsonPath.StepKind.Property)
        {
            if (Fallback is not null)
            {
                foreach (var token in SelectDeepOverlay(Root, Fallback, path))
                {
                    yield return token;
                }
            }
            else
            {
                foreach (var token in path.SelectTokens(Root))
                {
                    yield return token;
                }
            }

            yield break;
        }

        foreach (var token in ApplySteps(MaterializeStash(), path.Steps, 1))
        {
            yield return token;
        }
    }

    private JsonObject MaterializeStash()
    {
        JsonObject result;
        var primaryStash = (Root as JsonObject)?["stash"];
        var fallbackStash = (Fallback as JsonObject)?["stash"];

        if (primaryStash is JsonObject primaryObject)
        {
            result = fallbackStash is JsonObject fallbackObject
                ? (JsonObject)fallbackObject.DeepClone()
                : new JsonObject();
            MergeInto(result, primaryObject);
        }
        else if (primaryStash is not null || (Root as JsonObject)?.ContainsKey("stash") == true)
        {
            result = new JsonObject();
        }
        else
        {
            result = fallbackStash is JsonObject fallbackObject
                ? (JsonObject)fallbackObject.DeepClone()
                : new JsonObject();
        }

        foreach (var (key, value) in StashOverlay!)
        {
            // Sequential stash assignments replace an existing value at the same key;
            // they do not recursively merge with the previous value.
            result[key] = value?.DeepClone();
        }

        return result;
    }

    private bool TrySelectRuntime(
        JsonPath.CompiledPath path,
        WorkflowRuntimeEvaluationOverlay runtime,
        out IEnumerable<JsonNode?> tokens)
    {
        tokens = [];
        if (path.Steps.Count == 0
            || path.Steps[0].Kind != JsonPath.StepKind.Property)
        {
            return false;
        }

        switch (path.Steps[0].Name)
        {
            case "workflow":
                tokens = SelectWorkflowRuntime(path, runtime);
                return true;

            case "workflowInstanceId" when !string.IsNullOrWhiteSpace(runtime.WorkflowInstanceId):
                tokens = ApplySteps(
                    JsonValue.Create(runtime.WorkflowInstanceId),
                    path.Steps,
                    1);
                return true;

            case "event" when runtime.HasPrimaryEvent
                                  || runtime.HasFallbackEvent:
                tokens = SelectRuntimeEvent(path, runtime);
                return true;

            default:
                return false;
        }
    }

    private static IEnumerable<JsonNode?> SelectWorkflowRuntime(
        JsonPath.CompiledPath path,
        WorkflowRuntimeEvaluationOverlay runtime)
    {
        if (path.Steps.Count == 1)
        {
            yield return runtime.MaterializeWorkflow();
            yield break;
        }

        var member = path.Steps[1];
        if (member.Kind != JsonPath.StepKind.Property)
        {
            foreach (var token in ApplySteps(runtime.MaterializeWorkflow(), path.Steps, 1))
            {
                yield return token;
            }

            yield break;
        }

        JsonNode? value;
        var hasValue = true;
        switch (member.Name)
        {
            case "workflowInstanceId" when !string.IsNullOrWhiteSpace(runtime.WorkflowInstanceId):
                value = JsonValue.Create(runtime.WorkflowInstanceId);
                break;

            case "workflowType" when !string.IsNullOrWhiteSpace(runtime.WorkflowType):
                value = JsonValue.Create(runtime.WorkflowType);
                break;

            case "event":
            case "resumeEvent":
                foreach (var token in SelectRuntimeWorkflowEvent(path, runtime))
                {
                    yield return token;
                }

                yield break;

            default:
                value = null;
                hasValue = false;
                break;
        }

        if (!hasValue)
        {
            yield break;
        }

        foreach (var token in ApplySteps(value, path.Steps, 2))
        {
            yield return token;
        }
    }

    private static IEnumerable<JsonNode?> SelectRuntimeWorkflowEvent(
        JsonPath.CompiledPath path,
        WorkflowRuntimeEvaluationOverlay runtime)
    {
        foreach (var token in SelectOverlayValues(
                     runtime.PrimaryEvent,
                     runtime.HasPrimaryEvent,
                     runtime.FallbackEvent,
                     runtime.HasFallbackEvent,
                     path.Steps,
                     2))
        {
            yield return token;
        }
    }

    private IEnumerable<JsonNode?> SelectRuntimeEvent(
        JsonPath.CompiledPath path,
        WorkflowRuntimeEvaluationOverlay runtime)
    {
        var primaryEvent = runtime.HasPrimaryEvent
            ? runtime.PrimaryEvent
            : TryGetRootProperty(Root, "event", out var primaryValue)
                ? primaryValue
                : null;
        var hasPrimaryEvent = runtime.HasPrimaryEvent
                              || TryGetRootProperty(Root, "event", out _);

        var fallbackEvent = runtime.HasFallbackEvent
            ? runtime.FallbackEvent
            : TryGetRootProperty(Fallback, "event", out var fallbackValue)
                ? fallbackValue
                : null;
        var hasFallbackEvent = runtime.HasFallbackEvent
                               || TryGetRootProperty(Fallback, "event", out _);

        foreach (var token in SelectOverlayValues(
                     primaryEvent,
                     hasPrimaryEvent,
                     fallbackEvent,
                     hasFallbackEvent,
                     path.Steps,
                     1))
        {
            yield return token;
        }
    }

    private static IEnumerable<JsonNode?> SelectOverlayValues(
        JsonNode? primary,
        bool hasPrimary,
        JsonNode? fallback,
        bool hasFallback,
        IReadOnlyList<JsonPath.Step> steps,
        int startIndex)
    {
        IEnumerable<OverlayCandidate> current =
        [
            new OverlayCandidate(primary, hasPrimary, fallback, hasFallback)
        ];
        for (var index = startIndex; index < steps.Count; index++)
        {
            current = ApplyOverlayStep(current, steps[index]);
        }

        foreach (var candidate in current)
        {
            yield return candidate.HasPrimary ? candidate.Primary : candidate.Fallback;
        }
    }

    private static IEnumerable<JsonNode?> ApplySteps(
        JsonNode? root,
        IReadOnlyList<JsonPath.Step> steps,
        int startIndex)
    {
        IEnumerable<JsonNode?> current = [root];
        for (var index = startIndex; index < steps.Count; index++)
        {
            current = steps[index].Apply(current);
        }

        return current;
    }

    private static bool TryGetRootProperty(
        JsonNode? root,
        string name,
        out JsonNode? value)
    {
        if (root is JsonObject obj)
        {
            return obj.TryGetPropertyValue(name, out value);
        }

        value = null;
        return false;
    }

    private static IEnumerable<JsonNode?> SelectDeepOverlay(
        JsonNode primary,
        JsonNode fallback,
        JsonPath.CompiledPath path)
    {
        if (path.Steps.Count == 0)
        {
            if (path.IsRoot)
            {
                // A root expression needs an actual merged object to preserve its public
                // value shape. Normal path traversal remains entirely clone-free.
                if (primary is JsonObject primaryObject && fallback is JsonObject fallbackObject)
                {
                    var merged = (JsonObject)fallbackObject.DeepClone();
                    MergeInto(merged, primaryObject);
                    yield return merged;
                }
                else
                {
                    yield return primary;
                }
            }

            yield break;
        }

        IEnumerable<OverlayCandidate> current =
        [
            new OverlayCandidate(primary, true, fallback, true)
        ];
        foreach (var step in path.Steps)
        {
            current = ApplyOverlayStep(current, step);
        }

        foreach (var candidate in current)
        {
            yield return candidate.HasPrimary ? candidate.Primary : candidate.Fallback;
        }
    }

    private static IEnumerable<OverlayCandidate> ApplyOverlayStep(
        IEnumerable<OverlayCandidate> candidates,
        JsonPath.Step step)
    {
        foreach (var candidate in candidates)
        {
            foreach (var result in step.Kind switch
                     {
                         JsonPath.StepKind.Property => ApplyOverlayProperty(candidate, step.Name!),
                         JsonPath.StepKind.Index => ApplyOverlayIndex(candidate, step.Index),
                         JsonPath.StepKind.Wildcard => ApplyOverlayWildcard(candidate),
                         _ => []
                     })
            {
                yield return result;
            }
        }
    }

    private static IEnumerable<OverlayCandidate> ApplyOverlayProperty(
        OverlayCandidate candidate,
        string name)
    {
        if (!candidate.HasPrimary)
        {
            foreach (var fallback in ApplyProperty(candidate.Fallback, name))
            {
                yield return new OverlayCandidate(null, false, fallback, true);
            }

            yield break;
        }

        if (candidate.Primary is JsonObject primaryObject)
        {
            var hasPrimaryProperty = primaryObject.TryGetPropertyValue(name, out var primaryValue);
            JsonNode? fallbackValue = null;
            var hasFallbackProperty = candidate.HasFallback
                                      && candidate.Fallback is JsonObject fallbackObject
                                      && fallbackObject.TryGetPropertyValue(name, out fallbackValue);

            if (hasPrimaryProperty)
            {
                if (primaryValue is JsonObject
                    && hasFallbackProperty
                    && fallbackValue is JsonObject)
                {
                    yield return new OverlayCandidate(primaryValue, true, fallbackValue, true);
                }
                else
                {
                    yield return new OverlayCandidate(primaryValue, true, null, false);
                }
            }
            else if (hasFallbackProperty)
            {
                yield return new OverlayCandidate(null, false, fallbackValue, true);
            }

            yield break;
        }

        // A primary array/scalar/null replaces the fallback subtree completely.
        foreach (var primary in ApplyProperty(candidate.Primary, name))
        {
            yield return new OverlayCandidate(primary, true, null, false);
        }
    }

    private static IEnumerable<OverlayCandidate> ApplyOverlayIndex(
        OverlayCandidate candidate,
        int index)
    {
        var source = candidate.HasPrimary ? candidate.Primary : candidate.Fallback;
        foreach (var selected in ApplyIndex(source, index))
        {
            yield return candidate.HasPrimary
                ? new OverlayCandidate(selected, true, null, false)
                : new OverlayCandidate(null, false, selected, true);
        }
    }

    private static IEnumerable<OverlayCandidate> ApplyOverlayWildcard(OverlayCandidate candidate)
    {
        if (!candidate.HasPrimary)
        {
            foreach (var fallback in ApplyWildcard(candidate.Fallback))
            {
                yield return new OverlayCandidate(null, false, fallback, true);
            }

            yield break;
        }

        if (candidate.Primary is JsonObject primaryObject)
        {
            var fallbackObject = candidate.HasFallback ? candidate.Fallback as JsonObject : null;
            if (fallbackObject is not null)
            {
                // Deep merging starts from a clone of fallback, so overwritten properties
                // retain fallback insertion order and primary-only properties append later.
                foreach (var (name, fallbackValue) in fallbackObject)
                {
                    if (primaryObject.TryGetPropertyValue(name, out var primaryValue))
                    {
                        yield return primaryValue is JsonObject && fallbackValue is JsonObject
                            ? new OverlayCandidate(primaryValue, true, fallbackValue, true)
                            : new OverlayCandidate(primaryValue, true, null, false);
                    }
                    else
                    {
                        yield return new OverlayCandidate(null, false, fallbackValue, true);
                    }
                }
            }

            foreach (var (name, primaryValue) in primaryObject)
            {
                if (fallbackObject is null || !fallbackObject.ContainsKey(name))
                {
                    yield return new OverlayCandidate(primaryValue, true, null, false);
                }
            }

            yield break;
        }

        foreach (var primary in ApplyWildcard(candidate.Primary))
        {
            yield return new OverlayCandidate(primary, true, null, false);
        }
    }

    private static IEnumerable<JsonNode?> ApplyProperty(JsonNode? node, string name)
    {
        if (node is JsonObject obj)
        {
            if (obj.TryGetPropertyValue(name, out var value))
            {
                yield return value;
            }

            yield break;
        }

        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is JsonObject itemObject
                    && itemObject.TryGetPropertyValue(name, out var value))
                {
                    yield return value;
                }
            }
        }
    }

    private static IEnumerable<JsonNode?> ApplyIndex(JsonNode? node, int index)
    {
        if (node is JsonArray array)
        {
            var normalizedIndex = index < 0 ? array.Count + index : index;
            if (normalizedIndex >= 0 && normalizedIndex < array.Count)
            {
                yield return array[normalizedIndex];
            }

            yield break;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            var normalizedIndex = index < 0 ? text.Length + index : index;
            if (normalizedIndex >= 0 && normalizedIndex < text.Length)
            {
                yield return JsonValue.Create(text[normalizedIndex].ToString());
            }
        }
    }

    private static IEnumerable<JsonNode?> ApplyWildcard(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            foreach (var value in array)
            {
                yield return value;
            }

            yield break;
        }

        if (node is JsonObject obj)
        {
            foreach (var value in obj.Select(property => property.Value))
            {
                yield return value;
            }
        }
    }

    private static void MergeInto(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            if (target[key] is JsonObject targetObject && value is JsonObject sourceObject)
            {
                MergeInto(targetObject, sourceObject);
                continue;
            }

            target[key] = value?.DeepClone();
        }
    }

    private readonly record struct OverlayCandidate(
        JsonNode? Primary,
        bool HasPrimary,
        JsonNode? Fallback,
        bool HasFallback);

    private static IEnumerable<JsonNode?> SelectOverlay(JsonNode? overlay, string remainder)
    {
        if (remainder.Length == 0)
        {
            yield return overlay;
            yield break;
        }

        foreach (var token in JsonPath.SelectTokens(overlay, $"${remainder}"))
        {
            yield return token;
        }
    }

    private static IEnumerable<JsonNode?> SelectItem(JsonNode? item, string remainder)
    {
        if (item is JsonValue || item is null)
        {
            if (remainder.Length == 0)
            {
                yield return new JsonObject
                {
                    ["value"] = item?.DeepClone()
                };
                yield break;
            }

            if (TryRemoveValueProperty(remainder, out var valueRemainder))
            {
                if (valueRemainder.Length == 0)
                {
                    yield return item;
                    yield break;
                }

                foreach (var token in JsonPath.SelectTokens(item, $"${valueRemainder}"))
                {
                    yield return token;
                }
            }

            yield break;
        }

        foreach (var token in SelectOverlay(item, remainder))
        {
            yield return token;
        }
    }

    internal static bool TryReadRootProperty(string path, string propertyName, out string remainder)
    {
        remainder = string.Empty;
        var span = path.AsSpan().Trim();
        if (span.Length == 0 || span[0] != '$')
        {
            return false;
        }

        var position = 1;
        SkipWhitespace(span, ref position);

        if (position < span.Length && span[position] == '.')
        {
            position++;
            var start = position;
            while (position < span.Length && IsIdentifierCharacter(span[position]))
            {
                position++;
            }

            if (!span[start..position].Equals(propertyName, StringComparison.Ordinal))
            {
                return false;
            }
        }
        else if (position < span.Length && span[position] == '[')
        {
            position++;
            SkipWhitespace(span, ref position);
            if (position >= span.Length || span[position] is not ('\'' or '"'))
            {
                return false;
            }

            var quote = span[position++];
            var start = position;
            while (position < span.Length && span[position] != quote)
            {
                if (span[position] == '\\')
                {
                    return false;
                }

                position++;
            }

            if (position >= span.Length
                || !span[start..position].Equals(propertyName, StringComparison.Ordinal))
            {
                return false;
            }

            position++;
            SkipWhitespace(span, ref position);
            if (position >= span.Length || span[position] != ']')
            {
                return false;
            }

            position++;
        }
        else
        {
            return false;
        }

        if (position < span.Length && span[position] is not ('.' or '['))
        {
            return false;
        }

        remainder = span[position..].ToString();
        return true;
    }

    private static bool TryRemoveValueProperty(string remainder, out string valueRemainder)
    {
        valueRemainder = string.Empty;
        if (!TryReadRootProperty($"${remainder}", "value", out valueRemainder))
        {
            return false;
        }

        return true;
    }

    private static void SkipWhitespace(ReadOnlySpan<char> span, ref int position)
    {
        while (position < span.Length && char.IsWhiteSpace(span[position]))
        {
            position++;
        }
    }

    private static bool IsIdentifierCharacter(char character)
        => char.IsLetterOrDigit(character) || character is '_' or '$';
}

/// <summary>
/// Runtime-only mapping values which used to be added by cloning and enriching the
/// complete workflow payload. Event nodes are borrowed and are never attached to
/// another <see cref="JsonNode"/> tree.
/// </summary>
internal sealed record WorkflowRuntimeEvaluationOverlay(
    string? WorkflowType,
    string? WorkflowInstanceId,
    JsonObject? PrimaryEvent,
    bool HasPrimaryEvent,
    JsonObject? FallbackEvent = null,
    bool HasFallbackEvent = false)
{
    internal JsonObject MaterializeWorkflow()
    {
        var workflow = new JsonObject();
        if (!string.IsNullOrWhiteSpace(WorkflowInstanceId))
        {
            workflow["workflowInstanceId"] = WorkflowInstanceId;
        }

        if (!string.IsNullOrWhiteSpace(WorkflowType))
        {
            workflow["workflowType"] = WorkflowType;
        }

        var eventNode = PrimaryEvent ?? FallbackEvent;
        if (eventNode is not null)
        {
            workflow["event"] = eventNode.DeepClone();
            workflow["resumeEvent"] = eventNode.DeepClone();
        }

        return workflow;
    }
}
