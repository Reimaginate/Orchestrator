using System.Text.Json;
using System.Text.Json.Nodes;
using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowEventNormalization;

public sealed class WorkflowEventMessageNormalizationService : IWorkflowEventMessageNormalizationService
{
    #region Public API

    public WorkflowEventMessage Normalize(BinaryData body, string? messageId, IReadOnlyDictionary<string, object?>? applicationProperties)
    {
        // Start with a case-insensitive copy so downstream lookups are consistent across transport implementations.
        var properties = applicationProperties is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(applicationProperties, StringComparer.OrdinalIgnoreCase);

        var bodyText = body.ToString();
        var parsedBody = TryParseJson(bodyText);

        if (parsedBody is JsonObject cloudEvent && LooksLikeCloudEvent(cloudEvent))
        {
            // Prefer native CloudEvent payloads when present.
            return NormalizeStructuredCloudEvent(cloudEvent, messageId, properties);
        }

        if (parsedBody is JsonObject eventGridEvent && LooksLikeEventGridEvent(eventGridEvent))
        {
            // Event Grid envelopes are normalized into the same internal CloudEvent-like model.
            return NormalizeEventGridEvent(eventGridEvent, messageId, properties);
        }

        // Fallback path for existing legacy producer payloads and plain text messages.
        return NormalizeLegacyEvent(bodyText, parsedBody, messageId, properties);
    }

    public JsonObject ToCloudEventPayload(WorkflowEventMessage message)
    {
        var cloudEvent = new JsonObject
        {
            ["specversion"] = message.SpecVersion,
            ["id"] = message.Id,
            ["source"] = message.Source,
            ["type"] = message.Type,
            ["subject"] = message.Subject,
            ["time"] = message.Time?.ToString("O"),
            ["datacontenttype"] = message.DataContentType,
            ["dataschema"] = message.DataSchema,
            ["data"] = message.Data.DeepClone()
        };

        foreach (var extension in message.Extensions)
        {
            if (cloudEvent.ContainsKey(extension.Key))
            {
                continue;
            }

            cloudEvent[extension.Key] = JsonValue.Create(extension.Value?.ToString());
        }

        return cloudEvent;
    }

    #endregion

    #region Message format normalization

    private static WorkflowEventMessage NormalizeEventGridEvent(
        JsonObject eventGridEvent,
        string? messageId,
        IReadOnlyDictionary<string, object?> properties)
    {
        var id = TryGetString(eventGridEvent, "id")
                 ?? TryGetString(properties, "id")
                 ?? messageId
                 ?? Guid.NewGuid().ToString("N");

        var source = TryGetString(eventGridEvent, "topic")
                     ?? TryGetString(properties, "eventSource")
                     ?? TryGetString(properties, "source")
                     ?? "urn:reimaginate:orchestrator:eventgrid";

        var type = TryGetString(eventGridEvent, "eventType")
                   ?? TryGetString(properties, "eventType")
                   ?? TryGetString(properties, "ce-type")
                   ?? "unknown";

        var data = NormalizeEventGridData(eventGridEvent["data"]);

        var extensions = new Dictionary<string, object?>(properties, StringComparer.OrdinalIgnoreCase)
        {
            ["eventGridDataVersion"] = TryGetString(eventGridEvent, "dataVersion"),
            ["eventGridMetadataVersion"] = TryGetString(eventGridEvent, "metadataVersion"),
            ["eventGridTopic"] = TryGetString(eventGridEvent, "topic")
        };

        return new WorkflowEventMessage
        {
            SpecVersion = "1.0",
            Id = id,
            Source = source,
            Type = type,
            Subject = TryGetString(eventGridEvent, "subject") ?? TryGetString(properties, "subject"),
            Time = TryGetDateTime(eventGridEvent, "eventTime")
                   ?? TryGetDateTime(eventGridEvent, "time")
                   ?? TryGetDateTime(properties, "time"),
            DataContentType = TryGetString(properties, "ce-datacontenttype") ?? "application/json",
            DataSchema = TryGetString(properties, "ce-dataschema"),
            Data = data,
            Extensions = extensions
        };
    }

    private static WorkflowEventMessage NormalizeStructuredCloudEvent(
        JsonObject cloudEvent,
        string? messageId,
        IReadOnlyDictionary<string, object?> properties)
    {
        var id = TryGetString(cloudEvent, "id")
                 ?? TryGetString(properties, "ce-id")
                 ?? TryGetString(properties, "id")
                 ?? messageId
                 ?? Guid.NewGuid().ToString("N");

        var source = TryGetString(cloudEvent, "source")
                     ?? TryGetString(properties, "ce-source")
                     ?? TryGetString(properties, "source")
                     ?? "urn:reimaginate:orchestrator:event";

        var type = TryGetString(cloudEvent, "type")
                   ?? TryGetString(properties, "ce-type")
                   ?? TryGetString(properties, "eventType")
                   ?? "unknown";

        var data = cloudEvent["data"]?.DeepClone() ?? JsonValue.Create(string.Empty)!;
        var extensionProperties = ExtractExtensions(cloudEvent, properties);

        return new WorkflowEventMessage
        {
            SpecVersion = TryGetString(cloudEvent, "specversion") ?? "1.0",
            Id = id,
            Source = source,
            Type = type,
            Subject = TryGetString(cloudEvent, "subject") ?? TryGetString(properties, "ce-subject"),
            Time = TryGetDateTime(cloudEvent, "time") ?? TryGetDateTime(properties, "ce-time") ?? TryGetDateTime(properties, "time"),
            DataContentType = TryGetString(cloudEvent, "datacontenttype") ?? TryGetString(properties, "ce-datacontenttype"),
            DataSchema = TryGetString(cloudEvent, "dataschema") ?? TryGetString(properties, "ce-dataschema"),
            Data = data,
            Extensions = extensionProperties
        };
    }

    private static WorkflowEventMessage NormalizeLegacyEvent(
        string bodyText,
        JsonNode? parsedBody,
        string? messageId,
        IReadOnlyDictionary<string, object?> properties)
    {
        var id = TryGetString(properties, "ce-id")
                 ?? TryGetString(properties, "id")
                 ?? messageId
                 ?? TryGetString(parsedBody as JsonObject, "id")
                 ?? Guid.NewGuid().ToString("N");

        var source = TryGetString(properties, "ce-source")
                     ?? TryGetString(properties, "eventSource")
                     ?? TryGetString(properties, "source")
                     ?? TryGetString(parsedBody as JsonObject, "source")
                     ?? "urn:reimaginate:orchestrator:event";

        var type = TryGetString(properties, "ce-type")
                   ?? TryGetString(properties, "eventType")
                   ?? TryGetString(properties, "action")
                   ?? TryGetString(parsedBody as JsonObject, "eventType")
                   ?? TryGetString(parsedBody as JsonObject, "action")
                   ?? TryGetString(parsedBody as JsonObject, "type")
                   ?? "unknown";

        return new WorkflowEventMessage
        {
            SpecVersion = TryGetString(properties, "ce-specversion") ?? "1.0",
            Id = id,
            Source = source,
            Type = type,
            Subject = TryGetString(properties, "ce-subject") ?? TryGetString(parsedBody as JsonObject, "subject"),
            Time = TryGetDateTime(properties, "ce-time") ?? TryGetDateTime(properties, "time") ?? TryGetDateTime(parsedBody as JsonObject, "time"),
            DataContentType = TryGetString(properties, "ce-datacontenttype") ?? (parsedBody is null ? "text/plain" : "application/json"),
            DataSchema = TryGetString(properties, "ce-dataschema"),
            Data = parsedBody?.DeepClone() ?? JsonValue.Create(bodyText)!,
            Extensions = properties
        };
    }

    #endregion

    #region Message shape detection and enrichment helpers

    private static bool LooksLikeCloudEvent(JsonObject jsonObject)
    {
        return jsonObject.ContainsKey("specversion")
            && jsonObject.ContainsKey("id")
            && jsonObject.ContainsKey("source")
            && jsonObject.ContainsKey("type");
    }

    private static bool LooksLikeEventGridEvent(JsonObject jsonObject)
    {
        return jsonObject.ContainsKey("eventType")
               && jsonObject.ContainsKey("data")
               && (jsonObject.ContainsKey("id") || jsonObject.ContainsKey("topic"));
    }

    private static JsonNode NormalizeEventGridData(JsonNode? dataNode)
    {
        if (dataNode is null)
        {
            return JsonValue.Create(string.Empty)!;
        }

        if (dataNode is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
        {
            var parsed = TryParseJson(text);
            return parsed?.DeepClone() ?? JsonValue.Create(text)!;
        }

        // Preserve non-string nodes (objects, arrays, numbers, booleans) as-is.
        return dataNode.DeepClone();
    }

    private static IReadOnlyDictionary<string, object?> ExtractExtensions(JsonObject cloudEvent, IReadOnlyDictionary<string, object?> properties)
    {
        var extensions = new Dictionary<string, object?>(properties, StringComparer.OrdinalIgnoreCase);
        var standardAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "id", "source", "specversion", "type", "subject", "time", "datacontenttype", "dataschema", "data"
        };

        foreach (var kvp in cloudEvent)
        {
            if (standardAttributes.Contains(kvp.Key))
            {
                continue;
            }

            extensions[kvp.Key] = kvp.Value?.ToJsonString();
        }

        return extensions;
    }

    #endregion

    #region Primitive parsing helpers

    private static JsonNode? TryParseJson(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryGetString(JsonObject? jsonObject, string key)
    {
        if (jsonObject is null || !jsonObject.TryGetPropertyValue(key, out var value) || value is null)
        {
            return null;
        }

        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var stringValue))
        {
            return stringValue;
        }

        return value.ToJsonString().Trim('"');
    }

    private static string? TryGetString(IReadOnlyDictionary<string, object?> properties, string key)
    {
        return properties.TryGetValue(key, out var value)
            ? value?.ToString()
            : null;
    }

    private static DateTimeOffset? TryGetDateTime(JsonObject? jsonObject, string key)
    {
        var value = TryGetString(jsonObject, key);
        return DateTimeOffset.TryParse(value, out var timestamp)
            ? timestamp
            : null;
    }

    private static DateTimeOffset? TryGetDateTime(IReadOnlyDictionary<string, object?> properties, string key)
    {
        var value = TryGetString(properties, key);
        return DateTimeOffset.TryParse(value, out var timestamp)
            ? timestamp
            : null;
    }

    #endregion
}
