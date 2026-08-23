using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reimaginate.Orchestrator.CommandLine.Inspect;

internal static class DiagnosticsCliOutput
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public static void WriteJson(JsonNode? node)
    {
        Console.WriteLine(node?.ToJsonString(SerializerOptions) ?? "null");
    }

    public static IReadOnlyList<JsonObject> ReadJsonLines(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var results = new List<JsonObject>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (JsonNode.Parse(line) is JsonObject parsed)
            {
                results.Add(parsed);
            }
        }

        return results;
    }

    public static JsonObject? ReadJsonObject(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
    }

    public static JsonObject? FindLatest(IReadOnlyList<JsonObject> entries, params string[] eventTypes)
    {
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            var eventType = entries[index]["eventType"]?.GetValue<string>();
            if (eventType is not null && eventTypes.Contains(eventType, StringComparer.Ordinal))
            {
                return entries[index];
            }
        }

        return null;
    }
}
