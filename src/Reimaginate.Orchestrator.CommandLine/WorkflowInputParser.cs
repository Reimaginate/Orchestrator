using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reimaginate.Orchestrator.CommandLine;

internal static class WorkflowInputParser
{
    public static JsonObject? Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var value = input.Trim();

        if (value.Length > 0 && value[0] == '{')
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var reader = new Utf8JsonReader(bytes, isFinalBlock: true, state: default);

            try
            {
                while (reader.Read())
                {
                }

                if (JsonNode.Parse(value) is JsonObject obj)
                {
                    return obj;
                }
            }
            catch (JsonException)
            {
                // fall through to wrapping below
            }
        }

        if (TryParseKeyValuePairs(value, out var pairObject))
        {
            return pairObject;
        }

        return WrapValue(value);
    }

    private static bool TryParseKeyValuePairs(string input, out JsonObject? pairObject)
    {
        pairObject = null;

        var segments = input.Split(',', StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        var result = new JsonObject();
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                return false;
            }

            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex <= 0)
            {
                return false;
            }

            var key = segment[..separatorIndex].Trim();
            if (key.Length == 0)
            {
                return false;
            }

            var value = segment[(separatorIndex + 1)..].Trim();
            result[key] = value;
        }

        pairObject = result;
        return true;
    }

    private static JsonObject WrapValue(string value)
    {
        return new JsonObject
        {
            ["Value"] = value
        };
    }
}
