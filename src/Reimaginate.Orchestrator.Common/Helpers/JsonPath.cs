using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Reimaginate.Orchestrator.Common.Helpers;

/// <summary>
/// Workflow-focused JSONPath-ish selector for JsonNode.
/// Supports:
/// - Root: $ (optional)
/// - Property: $.a.b  and  $['a b']
/// - Array index: [0], [-1]
/// - String index: $.SomeString[0] => JsonValue("S")
/// - Wildcard: [*] and .*
/// - Property projection over arrays: $.items.id (applies ".id" to each element if items is an array)
///
/// Notes:
/// - No recursive descent (..), unions, slices, or filters.
/// - SelectToken() returns the first match (or null). SelectTokens() yields all matches.
/// - Parsed paths are cached.
/// </summary>
public static class JsonPath
{
    public static JsonNode? SelectToken(JsonNode? root, string path)
        => SelectTokens(root, path).FirstOrDefault();

    public static IEnumerable<JsonNode?> SelectTokens(JsonNode? root, string path)
    {
        if (root is null) yield break;
        if (string.IsNullOrWhiteSpace(path)) yield break;

        foreach (var selected in Compile(path).SelectTokens(root))
        {
            yield return selected;
        }
    }

    internal static CompiledPath Compile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return _cache.GetOrAdd(path, static p => new CompiledPath(p, ParseSteps(p.AsSpan())));
    }

    internal sealed class CompiledPath
    {
        private readonly Step[] _steps;
        private readonly bool _isRoot;

        internal CompiledPath(string source, Step[] steps)
        {
            Source = source;
            _steps = steps;
            var trimmed = source.AsSpan().Trim();
            _isRoot = steps.Length == 0 && trimmed.Length == 1 && trimmed[0] == '$';
        }

        internal string Source { get; }
        internal IReadOnlyList<Step> Steps => _steps;
        internal bool IsRoot => _isRoot;

        internal JsonNode? SelectToken(JsonNode? root)
            => SelectTokens(root).FirstOrDefault();

        internal IEnumerable<JsonNode?> SelectTokens(JsonNode? root)
        {
            if (root is null)
            {
                yield break;
            }

            // If the parser couldn't understand the path, don't "match root" by accident.
            // Allow "$" (or whitespace around it) to explicitly mean "root".
            if (_steps.Length == 0)
            {
                if (_isRoot)
                {
                    yield return root;
                }

                yield break;
            }

            var current = EnumerateRoot(root);

            foreach (var step in _steps)
            {
                current = step.Apply(current);
            }

            foreach (var node in current)
            {
                yield return node;
            }
        }
    }

    // ----------------------------
    // Optional ergonomics
    // ----------------------------

    public static bool TrySelectToken(JsonNode? root, string path, out JsonNode? value)
    {
        value = SelectToken(root, path);
        return value is not null;
    }

    public static string? SelectString(JsonNode? root, string path)
    {
        var n = SelectToken(root, path);
        if (n is null) return null;

        if (n is JsonValue v)
        {
            try { return v.GetValue<string?>(); }
            catch { return n.ToString(); }
        }

        return n.ToString();
    }

    // ----------------------------
    // Compiled steps + cache
    // ----------------------------

    private static readonly ConcurrentDictionary<string, CompiledPath> _cache =
        new(StringComparer.Ordinal);

    private static IEnumerable<JsonNode?> EnumerateRoot(JsonNode root)
    {
        yield return root;
    }

    internal enum StepKind { Property, Index, Wildcard }

    internal readonly record struct Step(StepKind Kind, string? Name, int Index)
    {
        public IEnumerable<JsonNode?> Apply(IEnumerable<JsonNode?> input) => Kind switch
        {
            StepKind.Property => ApplyProperty(input, Name!),
            StepKind.Index => ApplyIndex(input, Index),
            StepKind.Wildcard => ApplyWildcard(input),
            _ => Enumerable.Empty<JsonNode?>()
        };

        private static IEnumerable<JsonNode?> ApplyProperty(IEnumerable<JsonNode?> input, string name)
        {
            foreach (var n in input)
            {
                if (n is null) continue;

                // Object property
                if (n is JsonObject o)
                {
                    if (o.TryGetPropertyValue(name, out var v))
                        yield return v;

                    continue;
                }

                // Projection over arrays (convenience): $.items.id
                if (n is JsonArray a)
                {
                    foreach (var item in a)
                    {
                        if (item is JsonObject o2 && o2.TryGetPropertyValue(name, out var v2))
                            yield return v2;
                    }
                }
            }
        }

        private static IEnumerable<JsonNode?> ApplyIndex(IEnumerable<JsonNode?> input, int index)
        {
            foreach (var n in input)
            {
                if (n is null) continue;

                // Array indexing
                if (n is JsonArray a)
                {
                    var idx = index < 0 ? a.Count + index : index;
                    if (idx >= 0 && idx < a.Count) yield return a[idx];
                    continue;
                }

                // String indexing: $.SomeString[0] => "S"
                if (n is JsonValue jv && TryGetString(jv, out var s))
                {
                    var idx = index < 0 ? s.Length + index : index;
                    if (idx >= 0 && idx < s.Length)
                        yield return JsonValue.Create(s[idx].ToString());
                }
            }
        }

        private static IEnumerable<JsonNode?> ApplyWildcard(IEnumerable<JsonNode?> input)
        {
            foreach (var n in input)
            {
                if (n is null) continue;

                if (n is JsonArray a)
                {
                    foreach (var v in a) yield return v;
                    continue;
                }

                if (n is JsonObject o)
                {
                    foreach (var kv in o) yield return kv.Value;
                }
            }
        }

        private static bool TryGetString(JsonValue v, out string s)
        {
            try
            {
                s = v.GetValue<string>();
                return true;
            }
            catch
            {
                s = "";
                return false;
            }
        }
    }

    // ----------------------------
    // Parser (minimal, allocation-aware)
    // ----------------------------

    private static Step[] ParseSteps(ReadOnlySpan<char> path)
    {
        var i = 0;
        SkipWs(path, ref i);

        // optional '$'
        if (i < path.Length && path[i] == '$')
            i++;

        var steps = new List<Step>(8);

        while (true)
        {
            SkipWs(path, ref i);
            if (i >= path.Length) break;

            // .name or .*
            if (path[i] == '.')
            {
                i++;
                SkipWs(path, ref i);

                if (i < path.Length && path[i] == '*')
                {
                    i++;
                    steps.Add(new Step(StepKind.Wildcard, null, 0));
                    continue;
                }

                var name = ParseIdentifier(path, ref i);
                steps.Add(new Step(StepKind.Property, name, 0));
                continue;
            }

            // [ ... ]
            if (path[i] == '[')
            {
                i++; // [
                SkipWs(path, ref i);

                // [*]
                if (i < path.Length && path[i] == '*')
                {
                    i++;
                    SkipWs(path, ref i);
                    Expect(path, ref i, ']');
                    steps.Add(new Step(StepKind.Wildcard, null, 0));
                    continue;
                }

                // ['a b'] or ["a b"]
                if (i < path.Length && (path[i] == '\'' || path[i] == '"'))
                {
                    var name = ParseQuoted(path, ref i);
                    SkipWs(path, ref i);
                    Expect(path, ref i, ']');
                    steps.Add(new Step(StepKind.Property, name, 0));
                    continue;
                }

                // [index] or [-1]
                var idx = ParseIntUntil(path, ref i, ']');
                Expect(path, ref i, ']');
                steps.Add(new Step(StepKind.Index, null, idx));
                continue;
            }

            // Unknown token: stop parsing (don’t throw for trailing junk; keep behavior forgiving)
            break;
        }

        return steps.ToArray();
    }

    private static void SkipWs(ReadOnlySpan<char> s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
    }

    private static void Expect(ReadOnlySpan<char> s, ref int i, char c)
    {
        SkipWs(s, ref i);
        if (i >= s.Length || s[i] != c)
            throw new FormatException($"Expected '{c}' at position {i}.");
        i++;
    }

    private static string ParseIdentifier(ReadOnlySpan<char> s, ref int i)
    {
        SkipWs(s, ref i);
        var start = i;

        while (i < s.Length)
        {
            var ch = s[i];
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '$')
                i++;
            else
                break;
        }

        if (start == i)
            throw new FormatException($"Expected identifier at position {i}.");

        return s.Slice(start, i - start).ToString();
    }

    private static string ParseQuoted(ReadOnlySpan<char> s, ref int i)
    {
        SkipWs(s, ref i);
        if (i >= s.Length) throw new FormatException("Unterminated string literal.");

        var quote = s[i];
        if (quote != '\'' && quote != '"')
            throw new FormatException($"Expected quoted string at position {i}.");

        i++; // skip quote

        // Fast path: no escapes
        var start = i;
        while (i < s.Length && s[i] != quote && s[i] != '\\') i++;

        if (i < s.Length && s[i] == quote)
        {
            var simple = s.Slice(start, i - start).ToString();
            i++; // skip quote
            return simple;
        }

        // Slow path: handle escapes
        var sb = new StringBuilder();

        while (i < s.Length)
        {
            var ch = s[i++];

            if (ch == quote)
                return sb.ToString();

            if (ch == '\\')
            {
                if (i >= s.Length) throw new FormatException("Unterminated escape sequence.");
                var esc = s[i++];
                sb.Append(esc switch
                {
                    '\\' => '\\',
                    '"' => '"',
                    '\'' => '\'',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => esc
                });
                continue;
            }

            sb.Append(ch);
        }

        throw new FormatException("Unterminated string literal.");
    }

    private static int ParseIntUntil(ReadOnlySpan<char> s, ref int i, char endChar)
    {
        SkipWs(s, ref i);
        var start = i;

        while (i < s.Length && s[i] != endChar) i++;

        if (i >= s.Length)
            throw new FormatException($"Unterminated segment, expected '{endChar}'.");

        var token = s.Slice(start, i - start).ToString().Trim();

        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            throw new FormatException($"Invalid index '{token}'.");

        return v;
    }
}
