using System.Text.Json.Nodes;

namespace Reimaginate.Orchestrator.Common.Helpers;

internal static class WorkflowStashHelper
{
    private const string StashPropertyName = "stash";

    public static JsonObject SeedStashFromInput(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var stash = EnsureStashObject(payload);

        foreach (var (key, value) in payload)
        {
            if (string.Equals(key, StashPropertyName, StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("__", StringComparison.Ordinal))
            {
                continue;
            }

            if (!stash.ContainsKey(key))
            {
                stash[key] = value?.DeepClone();
            }
        }

        return payload;
    }

    public static JsonObject MergeIntoStash(JsonObject payload, JsonObject? resolvedValues)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var stash = EnsureStashObject(payload);
        if (resolvedValues is null)
        {
            return payload;
        }

        foreach (var (key, value) in resolvedValues)
        {
            stash[key] = value?.DeepClone();
        }

        return payload;
    }

    public static JsonObject PreserveStash(JsonObject sourcePayload, JsonObject resultPayload)
    {
        ArgumentNullException.ThrowIfNull(sourcePayload);
        ArgumentNullException.ThrowIfNull(resultPayload);

        if (resultPayload.TryGetPropertyValue(StashPropertyName, out _))
        {
            return resultPayload;
        }

        if (sourcePayload[StashPropertyName] is JsonObject sourceStash)
        {
            resultPayload[StashPropertyName] = sourceStash.DeepClone();
        }

        return resultPayload;
    }

    public static JsonObject PreserveAndMergeOutputStash(JsonObject sourcePayload, JsonObject resultPayload, JsonObject? resolvedValues)
    {
        ArgumentNullException.ThrowIfNull(sourcePayload);
        ArgumentNullException.ThrowIfNull(resultPayload);

        PreserveStash(sourcePayload, resultPayload);
        return MergeIntoStash(resultPayload, resolvedValues);
    }

    public static JsonObject PreserveAndMergeOwnedOutputStash(JsonObject sourcePayload, JsonObject resultPayload, JsonObject? resolvedValues)
    {
        ArgumentNullException.ThrowIfNull(sourcePayload);
        ArgumentNullException.ThrowIfNull(resultPayload);

        if (resolvedValues is null)
        {
            return PreserveStash(sourcePayload, resultPayload);
        }

        if (!resultPayload.TryGetPropertyValue(StashPropertyName, out _)
            && sourcePayload[StashPropertyName] is JsonObject sourceStash)
        {
            var preservedStash = new JsonObject();
            foreach (var (key, value) in sourceStash)
            {
                // These exact, case-sensitive keys will be replaced below by owned
                // values, so cloning them would only create immediately discarded data.
                if (!resolvedValues.ContainsKey(key))
                {
                    preservedStash[key] = value?.DeepClone();
                }
            }

            resultPayload[StashPropertyName] = preservedStash;
        }

        var stash = EnsureStashObject(resultPayload);
        foreach (var key in resolvedValues.Select(entry => entry.Key).ToArray())
        {
            var value = resolvedValues[key];
            resolvedValues.Remove(key);
            stash[key] = value;
        }

        return resultPayload;
    }

    public static JsonObject OverlayOutputOnPayload(JsonObject sourcePayload, JsonObject resultPayload)
    {
        ArgumentNullException.ThrowIfNull(sourcePayload);
        ArgumentNullException.ThrowIfNull(resultPayload);

        var mergedPayload = (JsonObject)sourcePayload.DeepClone();
        foreach (var (key, value) in resultPayload)
        {
            if (string.Equals(key, StashPropertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            mergedPayload[key] = value?.DeepClone();
        }

        return mergedPayload;
    }

    public static JsonObject CreateInlineWorkflowInput(JsonObject parentPayload, JsonObject? overlay)
    {
        ArgumentNullException.ThrowIfNull(parentPayload);

        var mergedPayload = (JsonObject)parentPayload.DeepClone();
        if (overlay is not null)
        {
            MergeIntoPayload(mergedPayload, overlay);
        }

        MirrorRootValuesToStash(mergedPayload);
        return mergedPayload;
    }

    public static JsonObject MirrorRootValuesToStash(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var stash = EnsureStashObject(payload);
        foreach (var (key, value) in payload)
        {
            if (string.Equals(key, StashPropertyName, StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("__", StringComparison.Ordinal))
            {
                continue;
            }

            stash[key] = value?.DeepClone();
        }

        return payload;
    }

    private static JsonObject EnsureStashObject(JsonObject payload)
    {
        if (payload[StashPropertyName] is JsonObject stash)
        {
            return stash;
        }

        stash = new JsonObject();
        payload[StashPropertyName] = stash;
        return stash;
    }

    private static void MergeIntoPayload(JsonObject target, JsonObject overlay)
    {
        foreach (var (key, value) in overlay)
        {
            if (target[key] is JsonObject targetObject && value is JsonObject overlayObject)
            {
                MergeIntoPayload(targetObject, overlayObject);
                continue;
            }

            target[key] = value?.DeepClone();
        }
    }
}
