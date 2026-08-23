using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reimaginate.Orchestrator.Common.Helpers;

internal static class WorkflowExpressionFunctions
{
    public static JsonArray Pluck(IReadOnlyList<object?> args)
    {
        EnsureArgCount("pluck", args, 2);
        var propertyName = ReadRequiredString("pluck", args[1], "propertyName");
        var result = new JsonArray();

        foreach (var item in EnumerateArray(args[0]))
        {
            if (item is JsonObject obj && obj.ContainsKey(propertyName))
            {
                result.Add(obj[propertyName]?.DeepClone());
            }
        }

        return result;
    }

    public static JsonArray Select(IReadOnlyList<object?> args)
    {
        EnsureArgCount("select", args, 2);
        var path = ReadRequiredString("select", args[1], "jsonPath");
        _ = JsonPath.SelectTokens(new JsonObject(), path).FirstOrDefault();

        var result = new JsonArray();
        foreach (var item in EnumerateArray(args[0]))
        {
            foreach (var selected in JsonPath.SelectTokens(item, path))
            {
                result.Add(selected?.DeepClone());
            }
        }

        return result;
    }

    public static JsonArray Where(IReadOnlyList<object?> args, JsonObject? environment = null, JsonNode? outerContext = null)
        => Where(
            args,
            new WorkflowEvaluationScope(outerContext ?? new JsonObject(), environment));

    internal static JsonArray Where(IReadOnlyList<object?> args, WorkflowEvaluationScope scope)
    {
        EnsureArgCount("where", args, 2);
        var condition = ReadRequiredString("where", args[1], "condition");
        return Where(args, scope, WorkflowConditionEvaluator.Compile(condition));
    }

    internal static JsonArray Where(
        IReadOnlyList<object?> args,
        WorkflowEvaluationScope scope,
        CompiledWorkflowCondition compiledCondition)
    {
        EnsureArgCount("where", args, 2);

        var result = new JsonArray();
        foreach (var item in EnumerateArray(args[0]))
        {
            if (WorkflowConditionEvaluator.Evaluate(compiledCondition, scope.WithItem(item)))
            {
                result.Add(item?.DeepClone());
            }
        }

        return result;
    }

    public static JsonArray RemoveWhere(IReadOnlyList<object?> args, JsonObject? environment = null, JsonNode? outerContext = null)
        => RemoveWhere(
            args,
            new WorkflowEvaluationScope(outerContext ?? new JsonObject(), environment));

    internal static JsonArray RemoveWhere(IReadOnlyList<object?> args, WorkflowEvaluationScope scope)
    {
        EnsureArgCount("removeWhere", args, 2);
        var condition = ReadRequiredString("removeWhere", args[1], "condition");
        return RemoveWhere(args, scope, WorkflowConditionEvaluator.Compile(condition));
    }

    internal static JsonArray RemoveWhere(
        IReadOnlyList<object?> args,
        WorkflowEvaluationScope scope,
        CompiledWorkflowCondition compiledCondition)
    {
        EnsureArgCount("removeWhere", args, 2);

        var result = new JsonArray();
        foreach (var item in EnumerateArrayStrict("removeWhere", args[0]))
        {
            if (!WorkflowConditionEvaluator.Evaluate(compiledCondition, scope.WithItem(item)))
            {
                result.Add(item?.DeepClone());
            }
        }

        return result;
    }

    public static bool SetEquals(IReadOnlyList<object?> args)
    {
        EnsureArgCount("setEquals", args, 2);
        var left = ReadCanonicalSet("setEquals", args[0]);
        var right = ReadCanonicalSet("setEquals", args[1]);
        return left.SetEquals(right);
    }

    public static bool IsSubset(IReadOnlyList<object?> args)
    {
        EnsureArgCount("isSubset", args, 2);
        var left = ReadCanonicalSet("isSubset", args[0]);
        var right = ReadCanonicalSet("isSubset", args[1]);
        return left.IsSubsetOf(right);
    }

    public static JsonArray Compact(IReadOnlyList<object?> args)
    {
        EnsureArgCount("compact", args, 1);
        var result = new JsonArray();

        foreach (var item in EnumerateArray(args[0]))
        {
            if (IsNullOrEmptyString(item))
            {
                continue;
            }

            result.Add(item?.DeepClone());
        }

        return result;
    }

    public static JsonArray Distinct(IReadOnlyList<object?> args)
    {
        EnsureArgCount("distinct", args, 1);
        var result = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in EnumerateArray(args[0]))
        {
            var key = GetDistinctKey(item);
            if (seen.Add(key))
            {
                result.Add(item?.DeepClone());
            }
        }

        return result;
    }

    public static JsonNode? First(IReadOnlyList<object?> args)
    {
        EnsureArgCount("first", args, 1);
        return EnumerateArray(args[0]).FirstOrDefault()?.DeepClone();
    }

    public static JsonNode? Last(IReadOnlyList<object?> args)
    {
        EnsureArgCount("last", args, 1);
        return EnumerateArray(args[0]).LastOrDefault()?.DeepClone();
    }

    public static JsonNode? MaxBy(IReadOnlyList<object?> args)
    {
        EnsureArgCount("maxBy", args, 2);
        return FindBySelector(args, "maxBy", findMaximum: true);
    }

    public static JsonNode? MinBy(IReadOnlyList<object?> args)
    {
        EnsureArgCount("minBy", args, 2);
        return FindBySelector(args, "minBy", findMaximum: false);
    }

    public static JsonArray OrderBy(IReadOnlyList<object?> args)
    {
        EnsureArgCountInRange("orderBy", args, 2, 3);

        var selector = ReadSelector("orderBy", args[1]);
        var descending = false;
        if (args.Count == 3)
        {
            var direction = ReadRequiredString("orderBy", args[2], "direction");
            if (string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase))
            {
                descending = true;
            }
            else if (!string.Equals(direction, "asc", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Function 'orderBy' direction must be 'asc' or 'desc'.");
            }
        }

        var sortedItems = EnumerateArray(args[0])
            .Select((item, index) => new SelectorItem(item, ResolveSelectorValue(item, selector), index))
            .OrderBy(item => item, new SelectorItemComparer(descending));

        var result = new JsonArray();
        foreach (var sortedItem in sortedItems)
        {
            result.Add(sortedItem.Item?.DeepClone());
        }

        return result;
    }

    public static object? Coalesce(IReadOnlyList<object?> args)
    {
        EnsureArgCountInRange("coalesce", args, 1, int.MaxValue);
        foreach (var arg in args)
        {
            if (NormalizeValue(arg) is not null)
            {
                return CloneValue(arg);
            }
        }

        return null;
    }

    public static object? DefaultIfEmpty(IReadOnlyList<object?> args)
    {
        EnsureArgCount("defaultIfEmpty", args, 2);
        return IsEmpty(args[0]) ? CloneValue(args[1]) : CloneValue(args[0]);
    }

    public static string Join(IReadOnlyList<object?> args)
    {
        EnsureArgCount("join", args, 2);
        var separator = AsString(args[1]);
        return string.Join(separator, EnumerateArray(args[0]).Select(AsString));
    }

    public static JsonArray Split(IReadOnlyList<object?> args)
    {
        EnsureArgCount("split", args, 2);
        var text = AsString(args[0]);
        var separator = AsString(args[1]);
        if (separator.Length == 0)
        {
            throw new InvalidOperationException("Function 'split' separator cannot be empty.");
        }

        var result = new JsonArray();
        foreach (var part in text.Split(separator, StringSplitOptions.None))
        {
            result.Add(part);
        }

        return result;
    }

    public static JsonObject Merge(IReadOnlyList<object?> args)
    {
        EnsureArgCountInRange("merge", args, 2, int.MaxValue);

        var result = new JsonObject();
        foreach (var arg in args)
        {
            foreach (var (key, value) in ReadObject("merge", arg))
            {
                result[key] = value?.DeepClone();
            }
        }

        return result;
    }

    public static JsonObject Set(IReadOnlyList<object?> args)
    {
        EnsureArgCount("set", args, 3);

        var result = ReadObject("set", args[0]).DeepClone().AsObject();
        var propertyName = ReadRequiredString("set", args[1], "propertyName");
        result[propertyName] = CloneToJsonNode(args[2]);
        return result;
    }

    public static JsonArray Append(IReadOnlyList<object?> args)
    {
        EnsureArgCount("append", args, 2);

        var result = ReadArrayOrEmpty("append", args[0]);
        result.Add(CloneToJsonNode(args[1]));
        return result;
    }

    public static JsonArray Insert(IReadOnlyList<object?> args)
    {
        EnsureArgCount("insert", args, 3);

        var result = ReadArrayOrEmpty("insert", args[0]);
        var index = Math.Clamp(ReadInt32("insert", args[2], "index"), 0, result.Count);
        result.Insert(index, CloneToJsonNode(args[1]));
        return result;
    }

    public static JsonArray RemoveAt(IReadOnlyList<object?> args)
    {
        EnsureArgCount("removeAt", args, 2);

        var result = ReadArrayOrEmpty("removeAt", args[0]);
        var index = ReadInt32("removeAt", args[1], "index");
        if (index >= 0 && index < result.Count)
        {
            result.RemoveAt(index);
        }

        return result;
    }

    public static object? NormalizeValue(object? value)
    {
        value = WorkflowMissingValueHelper.NormalizeMissingToNull(value);

        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<bool>(out var b)) return b;
            if (jsonValue.TryGetValue<long>(out var l)) return l;
            if (jsonValue.TryGetValue<decimal>(out var d)) return d;
            if (jsonValue.TryGetValue<double>(out var dbl)) return dbl;
            if (jsonValue.TryGetValue<string>(out var s)) return s;
            return jsonValue.ToJsonString();
        }

        return value;
    }

    public static string AsString(object? value)
    {
        value = WorkflowMissingValueHelper.RequirePresent(value);
        var normalized = NormalizeValue(value);
        return normalized switch
        {
            null => string.Empty,
            JsonNode node => node.ToJsonString(),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => normalized.ToString() ?? string.Empty
        };
    }

    public static bool IsEmpty(object? value)
    {
        var normalized = NormalizeValue(value);
        return normalized switch
        {
            null => true,
            string s => s.Length == 0,
            JsonArray array => array.Count == 0,
            JsonObject obj => obj.Count == 0,
            IEnumerable<object?> enumerable => !enumerable.Any(),
            _ => false
        };
    }

    private static void EnsureArgCount(string functionName, IReadOnlyList<object?> args, int expected)
        => EnsureArgCountInRange(functionName, args, expected, expected);

    private static void EnsureArgCountInRange(string functionName, IReadOnlyList<object?> args, int min, int max)
    {
        if (args.Count < min || args.Count > max)
        {
            var expected = min == max ? $"exactly {min}" : $"between {min} and {max}";
            throw new InvalidOperationException($"Function '{functionName}' expects {expected} arguments.");
        }
    }

    private static string ReadRequiredString(string functionName, object? value, string argumentName)
    {
        var text = AsString(value);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException($"Function '{functionName}' requires a non-empty {argumentName} argument.");
        }

        return text;
    }

    private static IEnumerable<JsonNode?> EnumerateArray(object? value)
    {
        return NormalizeValue(value) is JsonArray array
            ? array
            : Enumerable.Empty<JsonNode?>();
    }

    private static IEnumerable<JsonNode?> EnumerateArrayStrict(string functionName, object? value)
        => ReadArrayOrEmpty(functionName, value);

    private static JsonObject ReadObject(string functionName, object? value)
    {
        if (WorkflowMissingValueHelper.IsMissing(value))
        {
            return new JsonObject();
        }

        var normalized = NormalizeValue(value);
        if (normalized is JsonObject obj)
        {
            return obj;
        }

        throw new InvalidOperationException($"Function '{functionName}' expects object arguments.");
    }

    private static JsonNode? FindBySelector(IReadOnlyList<object?> args, string functionName, bool findMaximum)
    {
        var selector = ReadSelector(functionName, args[1]);
        JsonNode? selectedItem = null;
        JsonNode? selectedValue = null;
        var hasSelectedItem = false;

        foreach (var item in EnumerateArray(args[0]))
        {
            var value = ResolveSelectorValue(item, selector);
            if (IsMissingSelectorValue(value))
            {
                continue;
            }

            var comparison = hasSelectedItem ? CompareSelectorValues(value, selectedValue) : 0;
            if (!hasSelectedItem || (findMaximum ? comparison > 0 : comparison < 0))
            {
                selectedItem = item;
                selectedValue = value;
                hasSelectedItem = true;
            }
        }

        return hasSelectedItem ? selectedItem?.DeepClone() : null;
    }

    private static JsonArray ReadArrayOrEmpty(string functionName, object? value)
    {
        var normalized = NormalizeValue(value);
        if (normalized is null)
        {
            return [];
        }

        if (normalized is JsonArray array)
        {
            var clone = array.DeepClone() as JsonArray;
            return clone ?? [];
        }

        throw new InvalidOperationException($"Function '{functionName}' expects the first argument to be an array or null.");
    }

    private static int ReadInt32(string functionName, object? value, string argumentName)
    {
        value = WorkflowMissingValueHelper.RequirePresent(value);
        var normalized = NormalizeValue(value);
        return normalized switch
        {
            byte b => b,
            sbyte sb => sb,
            short s => s,
            ushort us => us,
            int i => i,
            uint ui when ui <= int.MaxValue => (int)ui,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            ulong ul when ul <= int.MaxValue => (int)ul,
            decimal d when d == decimal.Truncate(d) && d is >= int.MinValue and <= int.MaxValue => (int)d,
            double dbl when Math.Truncate(dbl) == dbl && dbl is >= int.MinValue and <= int.MaxValue => (int)dbl,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => throw new InvalidOperationException($"Function '{functionName}' requires a valid integer {argumentName} argument.")
        };
    }

    private static string ReadSelector(string functionName, object? value)
    {
        var selector = ReadRequiredString(functionName, value, "selector");
        _ = JsonPath.SelectTokens(new JsonObject(), selector).FirstOrDefault();
        return selector;
    }

    private static JsonNode? ResolveSelectorValue(JsonNode? item, string selector)
    {
        var source = IsPredicateItemSelector(selector)
            ? BuildPredicateContext(item, null)
            : item ?? new JsonObject();

        return JsonPath.SelectToken(source, selector)?.DeepClone();
    }

    private static bool IsPredicateItemSelector(string selector)
        => string.Equals(selector, "$.item", StringComparison.Ordinal)
           || selector.StartsWith("$.item.", StringComparison.Ordinal)
           || selector.StartsWith("$.item[", StringComparison.Ordinal);

    private static bool IsMissingSelectorValue(JsonNode? value)
        => value is null
           || value is JsonValue jsonValue && NormalizeValue(jsonValue) is null;

    private static int CompareSelectorValues(JsonNode? left, JsonNode? right)
    {
        var leftMissing = IsMissingSelectorValue(left);
        var rightMissing = IsMissingSelectorValue(right);

        if (leftMissing || rightMissing)
        {
            return leftMissing == rightMissing ? 0 : leftMissing ? 1 : -1;
        }

        var normalizedLeft = NormalizeValue(left);
        var normalizedRight = NormalizeValue(right);

        if (TryAsDecimal(normalizedLeft, out var leftNumber) && TryAsDecimal(normalizedRight, out var rightNumber))
        {
            return leftNumber.CompareTo(rightNumber);
        }

        if (TryAsDateTimeOffset(normalizedLeft, out var leftTimestamp) && TryAsDateTimeOffset(normalizedRight, out var rightTimestamp))
        {
            return leftTimestamp.ToUniversalTime().CompareTo(rightTimestamp.ToUniversalTime());
        }

        if (normalizedLeft is bool leftBool && normalizedRight is bool rightBool)
        {
            return leftBool.CompareTo(rightBool);
        }

        return string.CompareOrdinal(AsString(normalizedLeft), AsString(normalizedRight));
    }

    private static bool TryAsDecimal(object? value, out decimal number)
    {
        value = NormalizeValue(value);
        switch (value)
        {
            case byte b:
                number = b;
                return true;
            case sbyte sb:
                number = sb;
                return true;
            case short s:
                number = s;
                return true;
            case ushort us:
                number = us;
                return true;
            case int i:
                number = i;
                return true;
            case uint ui:
                number = ui;
                return true;
            case long l:
                number = l;
                return true;
            case ulong ul:
                number = ul;
                return true;
            case decimal d:
                number = d;
                return true;
            case double dbl when !double.IsNaN(dbl) && !double.IsInfinity(dbl):
                number = Convert.ToDecimal(dbl, CultureInfo.InvariantCulture);
                return true;
            case float f when !float.IsNaN(f) && !float.IsInfinity(f):
                number = Convert.ToDecimal(f, CultureInfo.InvariantCulture);
                return true;
            case string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed):
                number = parsed;
                return true;
            default:
                number = 0;
                return false;
        }
    }

    private static bool TryAsDateTimeOffset(object? value, out DateTimeOffset timestamp)
    {
        value = NormalizeValue(value);
        timestamp = default;
        return value is string text
               && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp);
    }

    private static JsonObject BuildPredicateContext(JsonNode? item, JsonNode? outerContext)
    {
        var context = outerContext is JsonObject outerObject
            ? (JsonObject)outerObject.DeepClone()
            : new JsonObject();

        context["item"] = BuildPredicateItem(item);
        return context;
    }

    private static JsonNode? BuildPredicateItem(JsonNode? item)
    {
        if (item is null)
        {
            return new JsonObject
            {
                ["value"] = null
            };
        }

        if (item is JsonValue)
        {
            return new JsonObject
            {
                ["value"] = item.DeepClone()
            };
        }

        return item.DeepClone();
    }

    private static bool IsNullOrEmptyString(JsonNode? node)
    {
        if (node is null)
        {
            return true;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return string.IsNullOrWhiteSpace(text);
        }

        return false;
    }

    private static string GetDistinctKey(JsonNode? item)
    {
        if (item is null)
        {
            return "null:";
        }

        var normalized = NormalizeValue(item);
        return normalized switch
        {
            null => "null:",
            JsonNode node => $"json:{node.ToJsonString()}",
            string s => $"string:{s}",
            IFormattable formattable => $"{normalized.GetType().FullName}:{formattable.ToString(null, CultureInfo.InvariantCulture)}",
            _ => $"{normalized.GetType().FullName}:{normalized}"
        };
    }

    private static HashSet<string> ReadCanonicalSet(string functionName, object? value)
    {
        if (WorkflowMissingValueHelper.IsMissing(value) || NormalizeValue(value) is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        if (NormalizeValue(value) is not JsonArray array)
        {
            throw new InvalidOperationException(
                $"Function '{functionName}' expects array or null arguments.");
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array)
        {
            result.Add(GetSetKey(item));
        }

        return result;
    }

    private static string GetSetKey(JsonNode? item)
    {
        var normalized = NormalizeValue(item);
        if (normalized is null)
        {
            return "null:";
        }

        if (TryAsDecimal(normalized, out var number))
        {
            return $"number:{number.ToString("G29", CultureInfo.InvariantCulture)}";
        }

        return normalized switch
        {
            bool boolean => $"boolean:{boolean}",
            string text => $"string:{text}",
            JsonNode node => GetCanonicalJsonKey(node),
            IFormattable formattable =>
                $"{normalized.GetType().FullName}:{formattable.ToString(null, CultureInfo.InvariantCulture)}",
            _ => $"{normalized.GetType().FullName}:{normalized}"
        };
    }

    private static string GetCanonicalJsonKey(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            var properties = obj
                .OrderBy(property => property.Key, StringComparer.Ordinal)
                .Select(property =>
                    EncodeCanonicalSegment(JsonSerializer.Serialize(property.Key))
                    + EncodeCanonicalSegment(GetSetKey(property.Value)));
            return $"object:{{{string.Concat(properties)}}}";
        }

        if (node is JsonArray array)
        {
            return $"array:[{string.Concat(array.Select(item => EncodeCanonicalSegment(GetSetKey(item))))}]";
        }

        return GetSetKey(node);
    }

    private static string EncodeCanonicalSegment(string value)
        => $"{value.Length.ToString(CultureInfo.InvariantCulture)}:{value}";

    private static object? CloneValue(object? value)
        => value is JsonNode node ? node.DeepClone() : value;

    private static JsonNode? CloneToJsonNode(object? value)
    {
        value = WorkflowMissingValueHelper.RequirePresent(value);

        return value switch
        {
            null => null,
            JsonNode node => node.DeepClone(),
            _ => JsonSerializer.SerializeToNode(value)
        };
    }

    private sealed record SelectorItem(JsonNode? Item, JsonNode? Value, int Index);

    private sealed class SelectorItemComparer(bool descending) : IComparer<SelectorItem>
    {
        public int Compare(SelectorItem? x, SelectorItem? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return 1;
            }

            if (y is null)
            {
                return -1;
            }

            var xMissing = IsMissingSelectorValue(x.Value);
            var yMissing = IsMissingSelectorValue(y.Value);
            if (xMissing || yMissing)
            {
                var missingComparison = xMissing == yMissing ? 0 : xMissing ? 1 : -1;
                return missingComparison != 0
                    ? missingComparison
                    : x.Index.CompareTo(y.Index);
            }

            var valueComparison = CompareSelectorValues(x.Value, y.Value);
            if (descending)
            {
                valueComparison = -valueComparison;
            }

            return valueComparison != 0
                ? valueComparison
                : x.Index.CompareTo(y.Index);
        }
    }
}
