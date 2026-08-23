using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Reimaginate.Orchestrator.Common.Helpers;

internal enum WorkflowMapTargetSegmentKind
{
    Property,
    Index,
    Append,
    Wildcard
}

internal sealed record WorkflowMapTargetSegment(WorkflowMapTargetSegmentKind Kind, string? PropertyName = null, int? Index = null);

internal sealed record WorkflowMapTargetPath(IReadOnlyList<WorkflowMapTargetSegment> Segments)
{
    public bool ContainsWildcard => Segments.Any(static segment => segment.Kind == WorkflowMapTargetSegmentKind.Wildcard);
}

internal static class WorkflowMapTargetPathWriter
{
    public static WorkflowMapTargetPath Parse(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Map rule target must be a non-empty path.");
        }

        var span = path.AsSpan().Trim();
        var segments = new List<WorkflowMapTargetSegment>();
        var index = 0;

        if (index < span.Length && span[index] == '$')
        {
            index++;
            if (index < span.Length && span[index] == '.')
            {
                index++;
            }
        }

        while (index < span.Length)
        {
            SkipWhitespace(span, ref index);
            if (index >= span.Length)
            {
                break;
            }

            if (span[index] == '.')
            {
                index++;
                continue;
            }

            if (span[index] == '[')
            {
                index++;
                SkipWhitespace(span, ref index);

                if (index < span.Length && span[index] == ']')
                {
                    index++;
                    segments.Add(new WorkflowMapTargetSegment(WorkflowMapTargetSegmentKind.Append));
                    continue;
                }

                if (index < span.Length && span[index] == '*')
                {
                    index++;
                    SkipWhitespace(span, ref index);
                    Expect(span, ref index, ']');
                    segments.Add(new WorkflowMapTargetSegment(WorkflowMapTargetSegmentKind.Wildcard));
                    continue;
                }

                if (index < span.Length && (span[index] == '\'' || span[index] == '"'))
                {
                    var propertyName = ParseQuoted(span, ref index);
                    SkipWhitespace(span, ref index);
                    Expect(span, ref index, ']');
                    segments.Add(new WorkflowMapTargetSegment(WorkflowMapTargetSegmentKind.Property, propertyName));
                    continue;
                }

                var parsedIndex = ParseIntUntil(span, ref index, ']');
                Expect(span, ref index, ']');
                segments.Add(new WorkflowMapTargetSegment(WorkflowMapTargetSegmentKind.Index, Index: parsedIndex));
                continue;
            }

            var property = ParseIdentifier(span, ref index);
            segments.Add(new WorkflowMapTargetSegment(WorkflowMapTargetSegmentKind.Property, property));
        }

        if (segments.Count == 0)
        {
            throw new InvalidOperationException($"Map rule target '{path}' must address at least one property or array slot.");
        }

        return new WorkflowMapTargetPath(segments);
    }

    public static void Write(JsonObject root, string path, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(root);

        var parsed = Parse(path);
        if (parsed.ContainsWildcard)
        {
            throw new InvalidOperationException($"Map rule target '{path}' cannot use wildcard writes.");
        }

        Write(root, parsed, value);
    }

    public static void Write(JsonObject root, WorkflowMapTargetPath path, JsonNode? value)
        => WriteCore(root, path, value, cloneValue: true);

    internal static void WriteOwned(JsonObject root, WorkflowMapTargetPath path, JsonNode? value)
        => WriteCore(root, path, value, cloneValue: false);

    private static void WriteCore(JsonObject root, WorkflowMapTargetPath path, JsonNode? value, bool cloneValue)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);

        JsonNode current = root;
        for (var i = 0; i < path.Segments.Count; i++)
        {
            var segment = path.Segments[i];
            var isLast = i == path.Segments.Count - 1;
            var nextSegment = isLast ? null : path.Segments[i + 1];

            switch (segment.Kind)
            {
                case WorkflowMapTargetSegmentKind.Property:
                    if (current is not JsonObject currentObject)
                    {
                        throw new InvalidOperationException($"Map rule target cannot write property '{segment.PropertyName}' into a non-object node.");
                    }

                    if (isLast)
                    {
                        currentObject[segment.PropertyName!] = PrepareValue(value, cloneValue);
                        return;
                    }

                    if (currentObject[segment.PropertyName!] is not JsonNode child || !MatchesExpectedContainer(child, nextSegment!))
                    {
                        child = CreateContainerFor(nextSegment!);
                        currentObject[segment.PropertyName!] = child;
                    }

                    current = child;
                    break;

                case WorkflowMapTargetSegmentKind.Index:
                    if (current is not JsonArray currentArray)
                    {
                        throw new InvalidOperationException($"Map rule target cannot write array index [{segment.Index}] into a non-array node.");
                    }

                    EnsureIndex(currentArray, segment.Index!.Value);
                    if (isLast)
                    {
                        currentArray[segment.Index!.Value] = PrepareValue(value, cloneValue);
                        return;
                    }

                    if (currentArray[segment.Index!.Value] is not JsonNode indexedChild || !MatchesExpectedContainer(indexedChild, nextSegment!))
                    {
                        indexedChild = CreateContainerFor(nextSegment!);
                        currentArray[segment.Index!.Value] = indexedChild;
                    }

                    current = indexedChild;
                    break;

                case WorkflowMapTargetSegmentKind.Append:
                    if (current is not JsonArray appendArray)
                    {
                        throw new InvalidOperationException("Map rule target cannot append into a non-array node.");
                    }

                    if (isLast)
                    {
                        appendArray.Add(PrepareValue(value, cloneValue));
                        return;
                    }

                    var appendedChild = CreateContainerFor(nextSegment!);
                    appendArray.Add(appendedChild);
                    current = appendedChild;
                    break;

                case WorkflowMapTargetSegmentKind.Wildcard:
                    throw new InvalidOperationException("Map rule target cannot use wildcard writes.");
                default:
                    throw new InvalidOperationException($"Unsupported map target segment kind '{segment.Kind}'.");
            }
        }
    }

    private static JsonNode? PrepareValue(JsonNode? value, bool cloneValue)
        => cloneValue || value?.Parent is not null
            ? value?.DeepClone()
            : value;

    private static bool MatchesExpectedContainer(JsonNode node, WorkflowMapTargetSegment nextSegment)
    {
        return nextSegment.Kind switch
        {
            WorkflowMapTargetSegmentKind.Property => node is JsonObject,
            WorkflowMapTargetSegmentKind.Index or WorkflowMapTargetSegmentKind.Append or WorkflowMapTargetSegmentKind.Wildcard => node is JsonArray,
            _ => false
        };
    }

    private static JsonNode CreateContainerFor(WorkflowMapTargetSegment nextSegment)
    {
        return nextSegment.Kind switch
        {
            WorkflowMapTargetSegmentKind.Property => new JsonObject(),
            WorkflowMapTargetSegmentKind.Index or WorkflowMapTargetSegmentKind.Append or WorkflowMapTargetSegmentKind.Wildcard => new JsonArray(),
            _ => throw new InvalidOperationException($"Unsupported map target segment kind '{nextSegment.Kind}'.")
        };
    }

    private static void EnsureIndex(JsonArray array, int index)
    {
        if (index < 0)
        {
            throw new InvalidOperationException($"Map rule target cannot use negative array index '{index}'.");
        }

        while (array.Count <= index)
        {
            array.Add(null);
        }
    }

    private static void SkipWhitespace(ReadOnlySpan<char> span, ref int index)
    {
        while (index < span.Length && char.IsWhiteSpace(span[index]))
        {
            index++;
        }
    }

    private static void Expect(ReadOnlySpan<char> span, ref int index, char expected)
    {
        SkipWhitespace(span, ref index);
        if (index >= span.Length || span[index] != expected)
        {
            throw new InvalidOperationException($"Map rule target is invalid. Expected '{expected}' at position {index}.");
        }

        index++;
    }

    private static string ParseIdentifier(ReadOnlySpan<char> span, ref int index)
    {
        SkipWhitespace(span, ref index);
        var start = index;

        while (index < span.Length)
        {
            var c = span[index];
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '$')
            {
                index++;
                continue;
            }

            break;
        }

        if (start == index)
        {
            throw new InvalidOperationException($"Map rule target is invalid. Expected identifier at position {index}.");
        }

        return span[start..index].ToString();
    }

    private static string ParseQuoted(ReadOnlySpan<char> span, ref int index)
    {
        var quote = span[index];
        index++;
        var builder = new StringBuilder();

        while (index < span.Length)
        {
            var c = span[index++];
            if (c == '\\')
            {
                if (index >= span.Length)
                {
                    throw new InvalidOperationException("Map rule target contains an unterminated escape sequence.");
                }

                builder.Append(span[index++]);
                continue;
            }

            if (c == quote)
            {
                return builder.ToString();
            }

            builder.Append(c);
        }

        throw new InvalidOperationException("Map rule target contains an unterminated quoted property name.");
    }

    private static int ParseIntUntil(ReadOnlySpan<char> span, ref int index, char endChar)
    {
        SkipWhitespace(span, ref index);
        var start = index;
        while (index < span.Length && span[index] != endChar)
        {
            index++;
        }

        var token = span[start..index].ToString().Trim();
        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new InvalidOperationException($"Map rule target contains invalid array index '{token}'.");
        }

        return parsed;
    }
}
