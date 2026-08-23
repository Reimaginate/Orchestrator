using System.Text.Json.Nodes;

namespace Reimaginate.Orchestrator.Common.Helpers;

internal static class WorkflowEventPayloadContextHelper
{
    public static JsonObject BuildTriggerAuthoringPayload(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return ExtractBusinessPayload(payload);
    }

    public static JsonObject BuildListenContext(JsonObject rawPayload)
    {
        ArgumentNullException.ThrowIfNull(rawPayload);

        return new JsonObject
        {
            ["event"] = (JsonObject)rawPayload.DeepClone(),
            ["payload"] = ExtractBusinessPayload(rawPayload)
        };
    }

    public static JsonObject BuildListenContext(JsonObject rawPayload, JsonObject previous)
    {
        ArgumentNullException.ThrowIfNull(previous);

        var context = BuildListenContext(rawPayload);
        context["previous"] = (JsonObject)previous.DeepClone();
        return context;
    }

    private static JsonObject ExtractBusinessPayload(JsonObject payload)
    {
        if (IsCloudEvent(payload)
            && payload["data"] is JsonObject cloudEventData)
        {
            if (LooksLikeNestedEnvelope(cloudEventData)
                && cloudEventData["data"] is JsonObject nestedEnvelopeData)
            {
                return (JsonObject)nestedEnvelopeData.DeepClone();
            }

            return (JsonObject)cloudEventData.DeepClone();
        }

        return (JsonObject)payload.DeepClone();
    }

    private static bool IsCloudEvent(JsonObject payload)
    {
        return payload.ContainsKey("specversion")
            && payload.ContainsKey("id")
            && payload.ContainsKey("source")
            && payload.ContainsKey("type");
    }

    private static bool LooksLikeNestedEnvelope(JsonObject payload)
    {
        if (payload["data"] is not JsonObject)
        {
            return false;
        }

        return payload.ContainsKey("eventType")
            || payload.ContainsKey("type")
            || payload.ContainsKey("topic")
            || payload.ContainsKey("metadataVersion")
            || payload.ContainsKey("dataVersion");
    }
}
