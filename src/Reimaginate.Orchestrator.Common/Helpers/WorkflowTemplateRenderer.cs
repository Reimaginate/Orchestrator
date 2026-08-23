using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HandlebarsDotNet;

namespace Reimaginate.Orchestrator.Common.Helpers;

public static partial class WorkflowTemplateRenderer
{
    private static readonly IHandlebars Handlebars = CreateHandlebars();

    [GeneratedRegex("{{\\s*(\\$\\.[^{}]+?)\\s*}}", RegexOptions.CultureInvariant)]
    private static partial Regex JsonPathExpressionRegex();

    [GeneratedRegex("{{[^{}]*}}", RegexOptions.CultureInvariant)]
    private static partial Regex HandlebarsExpressionRegex();

    public static JsonNode? ResolveTemplate(string template, JsonObject context, JsonObject? fallbackContext = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(context);

        var trimmed = template.Trim();

        if (trimmed == "$" || trimmed.StartsWith("$.", StringComparison.Ordinal))
        {
            return SelectToken(context, fallbackContext, trimmed);
        }

        if (!template.Contains("{{", StringComparison.Ordinal))
        {
            return JsonValue.Create(template);
        }

        var normalizedTemplate = NormalizeJsonPathTemplate(template);
        var compiledTemplate = Handlebars.Compile(normalizedTemplate);
        var rendered = compiledTemplate(BuildHandlebarsContext(context));

        if (TryParseRenderedJsonNode(rendered, out var jsonNode))
        {
            return jsonNode;
        }

        return JsonValue.Create(rendered);
    }

    internal static JsonNode? ResolveTemplate(
        string template,
        WorkflowEvaluationScope scope,
        Func<JsonObject> legacyContextFactory)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(legacyContextFactory);

        var trimmed = template.Trim();
        if (trimmed == "$"
            || trimmed.StartsWith("$.", StringComparison.Ordinal)
            || trimmed.StartsWith("$[", StringComparison.Ordinal))
        {
            return scope.SelectToken(trimmed)?.DeepClone();
        }

        if (!template.Contains("{{", StringComparison.Ordinal))
        {
            return JsonValue.Create(template);
        }

        var pathMatches = JsonPathExpressionRegex().Matches(template);
        var handlebarsMatches = HandlebarsExpressionRegex().Matches(template);
        if (pathMatches.Count == 0 || pathMatches.Count != handlebarsMatches.Count)
        {
            // Non-JSON-path Handlebars forms retain their legacy behavior. The full
            // object graph is materialized only on this compatibility path.
            return ResolveTemplate(template, legacyContextFactory());
        }

        var rendered = JsonPathExpressionRegex().Replace(
            template,
            match => RenderSelectedToken(scope.SelectToken(match.Groups[1].Value)));

        if (TryParseRenderedJsonNode(rendered, out var jsonNode))
        {
            return jsonNode;
        }

        return JsonValue.Create(rendered);
    }

    private static string RenderSelectedToken(JsonNode? token)
    {
        return token switch
        {
            null => string.Empty,
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            _ => token.ToJsonString()
        };
    }


    private static JsonNode? SelectToken(JsonObject context, JsonObject? fallbackContext, string path)
    {
        var token = JsonPath.SelectToken(context, path);
        if (token is not null)
        {
            return token.DeepClone();
        }

        return fallbackContext is null ? null : JsonPath.SelectToken(fallbackContext, path)?.DeepClone();
    }

    private static IHandlebars CreateHandlebars()
    {
        var handlebars = HandlebarsDotNet.Handlebars.Create();

        handlebars.RegisterHelper("jsonPath", (writer, helperContext, parameters) =>
        {
            if (parameters.Length == 0)
            {
                writer.WriteSafeString(string.Empty);
                return;
            }

            var path = parameters[0]?.ToString();
            var source = parameters.Length > 1 ? ToJsonNode(parameters[1]) : ToJsonNode(helperContext.Value);

            if (string.IsNullOrWhiteSpace(path) || source is null)
            {
                writer.WriteSafeString(string.Empty);
                return;
            }

            var token = JsonPath.SelectToken(source, path);
            switch (token)
            {
                case null:
                    writer.WriteSafeString(string.Empty);
                    return;

                case JsonValue value when value.TryGetValue<string>(out var text):
                    writer.WriteSafeString(text);
                    return;

                default:
                    writer.WriteSafeString(token.ToJsonString());
                    break;
            }
        });

        return handlebars;
    }

    private static string NormalizeJsonPathTemplate(string template)
    {
        return JsonPathExpressionRegex().Replace(template, "{{jsonPath \"$1\"}}");
    }

    private static object? BuildHandlebarsContext(JsonNode node)
    {
        return node switch
        {
            JsonObject obj => obj.ToDictionary(kvp => kvp.Key, kvp => BuildHandlebarsContext(kvp.Value!)),
            JsonArray arr => arr.Select(item => item is null ? null! : BuildHandlebarsContext(item)).ToList(),
            JsonValue value => ConvertJsonValue(value),
            _ => string.Empty
        };
    }

    private static object? ConvertJsonValue(JsonValue value)
    {
        if (value.TryGetValue<string>(out var s)) return s;
        if (value.TryGetValue<bool>(out var b)) return b;
        if (value.TryGetValue<int>(out var i)) return i;
        if (value.TryGetValue<long>(out var l)) return l;
        if (value.TryGetValue<decimal>(out var d)) return d;
        if (value.TryGetValue<double>(out var dbl)) return dbl;
        return value.ToJsonString();
    }

    private static bool TryParseRenderedJsonNode(string rendered, out JsonNode? node)
    {
        var trimmed = rendered.Trim();

        if (trimmed.Length == 0)
        {
            node = JsonValue.Create(string.Empty);
            return true;
        }

        if (trimmed is "null" or "true" or "false")
        {
            node = JsonNode.Parse(trimmed);
            return true;
        }

        if (trimmed.StartsWith('{') || trimmed.StartsWith('[') ||
            double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            try
            {
                node = JsonNode.Parse(trimmed);
                return node is not null;
            }
            catch (JsonException)
            {
                node = null;
                return false;
            }
        }

        node = null;
        return false;
    }

    private static JsonNode? ToJsonNode(object? source)
    {
        return source switch
        {
            null => null,
            JsonNode jsonNode => jsonNode,
            _ => JsonSerializer.SerializeToNode(source)
        };
    }
}
