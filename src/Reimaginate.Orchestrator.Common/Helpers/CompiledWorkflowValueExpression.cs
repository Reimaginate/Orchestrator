using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Reimaginate.Orchestrator.Common.Helpers;

internal sealed class CompiledWorkflowValueExpression
{
    private readonly ValueExpressionNode _root;

    internal CompiledWorkflowValueExpression(string expression, ValueExpressionNode root)
    {
        Expression = expression;
        _root = root;
    }

    public string Expression { get; }

    internal JsonNode? Evaluate(WorkflowEvaluationScope scope, bool allowMissingPaths)
    {
        var state = new ValueExpressionEvaluationState(scope, Expression, allowMissingPaths);
        return ValueExpressionRuntime.ToJsonNode(_root.Evaluate(state), allowMissingPaths);
    }
}

internal static class WorkflowValueExpressionCompiler
{
    private static readonly ConcurrentDictionary<string, CompiledWorkflowValueExpression> Cache =
        new(StringComparer.Ordinal);

    public static CompiledWorkflowValueExpression Compile(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        var normalized = expression.Trim();
        return Cache.GetOrAdd(
            normalized,
            static value => new ValueExpressionCompilerParser(value).Compile());
    }

    internal static int CachedExpressionCount => Cache.Count;
}

internal readonly record struct ValueExpressionEvaluationState(
    WorkflowEvaluationScope Scope,
    string Expression,
    bool AllowMissingPaths);

internal abstract record ValueExpressionNode
{
    public abstract object? Evaluate(ValueExpressionEvaluationState state);
}

internal sealed record ValueExpressionLiteralNode(object? Value) : ValueExpressionNode
{
    public override object? Evaluate(ValueExpressionEvaluationState state) => Value;
}

internal sealed record ValueExpressionPathNode(
    string Path,
    JsonPath.CompiledPath CompiledPath) : ValueExpressionNode
{
    public override object? Evaluate(ValueExpressionEvaluationState state)
        => state.Scope.Resolve(CompiledPath, state.Expression);
}

internal sealed record ValueExpressionAddNode(
    ValueExpressionNode Left,
    ValueExpressionNode Right) : ValueExpressionNode
{
    public override object? Evaluate(ValueExpressionEvaluationState state)
        => ValueExpressionRuntime.Add(Left.Evaluate(state), Right.Evaluate(state), state);
}

internal sealed record ValueExpressionObjectNode(
    IReadOnlyList<KeyValuePair<string, ValueExpressionNode>> Properties) : ValueExpressionNode
{
    public override object? Evaluate(ValueExpressionEvaluationState state)
    {
        var result = new JsonObject();
        foreach (var property in Properties)
        {
            result[property.Key] = ValueExpressionRuntime.ToJsonNode(
                property.Value.Evaluate(state),
                state.AllowMissingPaths);
        }

        return result;
    }
}

internal sealed record ValueExpressionArrayNode(
    IReadOnlyList<ValueExpressionNode> Items) : ValueExpressionNode
{
    public override object? Evaluate(ValueExpressionEvaluationState state)
    {
        var result = new JsonArray();
        foreach (var item in Items)
        {
            result.Add(ValueExpressionRuntime.ToJsonNode(
                item.Evaluate(state),
                state.AllowMissingPaths));
        }

        return result;
    }
}

internal sealed record ValueExpressionFunctionNode(
    string Name,
    IReadOnlyList<ValueExpressionNode> Arguments,
    CompiledWorkflowCondition? Predicate) : ValueExpressionNode
{
    public override object? Evaluate(ValueExpressionEvaluationState state)
    {
        var args = new object?[Arguments.Count];
        for (var index = 0; index < Arguments.Count; index++)
        {
            args[index] = Arguments[index].Evaluate(state);
        }

        return ValueExpressionRuntime.EvaluateFunction(Name, args, Predicate, state);
    }
}

internal static class ValueExpressionRuntime
{
    public static object? Add(
        object? left,
        object? right,
        ValueExpressionEvaluationState state)
    {
        if (state.AllowMissingPaths
            && (WorkflowMissingValueHelper.IsMissing(left)
                || WorkflowMissingValueHelper.IsMissing(right)))
        {
            return left is WorkflowMissingValue ? left : right;
        }

        left = WorkflowMissingValueHelper.RequirePresent(left);
        right = WorkflowMissingValueHelper.RequirePresent(right);
        var normalizedLeft = NormalizeValue(left);
        var normalizedRight = NormalizeValue(right);
        if (TryAsNumber(normalizedLeft, out var leftNumber)
            && TryAsNumber(normalizedRight, out var rightNumber))
        {
            return leftNumber + rightNumber;
        }

        return $"{AsString(normalizedLeft)}{AsString(normalizedRight)}";
    }

    public static object? EvaluateFunction(
        string name,
        IReadOnlyList<object?> args,
        CompiledWorkflowCondition? predicate,
        ValueExpressionEvaluationState state)
    {
        try
        {
            return name switch
            {
                "startsWith" => AsString(args[0]).StartsWith(
                    AsString(args[1]),
                    ReadStringComparison(args, 2)),
                "endsWith" => AsString(args[0]).EndsWith(
                    AsString(args[1]),
                    ReadStringComparison(args, 2)),
                "contains" => AsString(args[0]).Contains(
                    AsString(args[1]),
                    ReadStringComparison(args, 2)),
                "matches" => EvaluateMatches(args),
                "toLower" => AsString(args[0]).ToLowerInvariant(),
                "toUpper" => AsString(args[0]).ToUpperInvariant(),
                "trim" => AsString(args[0]).Trim(),
                "exists" => EvaluateExists(args, state.Scope),
                "isNull" => NormalizeValue(args[0]) is null,
                "isNotNull" => NormalizeValue(args[0]) is not null,
                "isEmpty" => IsEmpty(args[0]),
                "isNotEmpty" => !IsEmpty(args[0]),
                "in" => EvaluateIn(args),
                "count" => Count(args[0]),
                "any" => Any(args[0]),
                "all" => All(args[0]),
                "pluck" => WorkflowExpressionFunctions.Pluck(args),
                "select" => WorkflowExpressionFunctions.Select(args),
                "where" => WorkflowExpressionFunctions.Where(args, state.Scope, predicate!),
                "compact" => WorkflowExpressionFunctions.Compact(args),
                "distinct" => WorkflowExpressionFunctions.Distinct(args),
                "setEquals" => WorkflowExpressionFunctions.SetEquals(args),
                "isSubset" => WorkflowExpressionFunctions.IsSubset(args),
                "first" => WorkflowExpressionFunctions.First(args),
                "last" => WorkflowExpressionFunctions.Last(args),
                "maxBy" => WorkflowExpressionFunctions.MaxBy(args),
                "minBy" => WorkflowExpressionFunctions.MinBy(args),
                "orderBy" => WorkflowExpressionFunctions.OrderBy(args),
                "coalesce" => WorkflowExpressionFunctions.Coalesce(args),
                "defaultIfEmpty" => WorkflowExpressionFunctions.DefaultIfEmpty(args),
                "join" => WorkflowExpressionFunctions.Join(args),
                "split" => WorkflowExpressionFunctions.Split(args),
                "merge" => WorkflowExpressionFunctions.Merge(args),
                "set" => WorkflowExpressionFunctions.Set(args),
                "append" => WorkflowExpressionFunctions.Append(args),
                "insert" => WorkflowExpressionFunctions.Insert(args),
                "removeWhere" => WorkflowExpressionFunctions.RemoveWhere(args, state.Scope, predicate!),
                "removeAt" => WorkflowExpressionFunctions.RemoveAt(args),
                _ => throw new InvalidOperationException(
                    $"Unsupported value function '{name}' in expression '{state.Expression}'.")
            };
        }
        catch (WorkflowMissingValueException) when (state.AllowMissingPaths)
        {
            return new WorkflowMissingValue("$", state.Expression);
        }
    }

    public static JsonNode? ToJsonNode(object? value, bool allowMissingPaths)
    {
        if (allowMissingPaths && WorkflowMissingValueHelper.IsMissing(value))
        {
            return null;
        }

        value = WorkflowMissingValueHelper.RequirePresent(value);
        return value switch
        {
            null => null,
            JsonNode node => node.DeepClone(),
            _ => JsonSerializer.SerializeToNode(value)
        };
    }

    private static bool EvaluateMatches(IReadOnlyList<object?> args)
    {
        var options = args.Count >= 3 && ToBoolean(args[2])
            ? RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
            : RegexOptions.CultureInvariant;
        return Regex.IsMatch(AsString(args[0]), AsString(args[1]), options);
    }

    private static bool EvaluateExists(
        IReadOnlyList<object?> args,
        WorkflowEvaluationScope scope)
    {
        if (args[0] is string path && path.StartsWith("$", StringComparison.Ordinal))
        {
            using var enumerator = scope.SelectTokens(path).GetEnumerator();
            return enumerator.MoveNext();
        }

        return NormalizeValue(args[0]) is not null;
    }

    private static bool EvaluateIn(IReadOnlyList<object?> args)
    {
        var probe = NormalizeValue(WorkflowMissingValueHelper.RequirePresent(args[0]));
        if (args.Count == 2
            && NormalizeValue(WorkflowMissingValueHelper.RequirePresent(args[1])) is JsonArray array)
        {
            return array.Any(item => ValuesEqual(probe, NormalizeValue(item)));
        }

        for (var index = 1; index < args.Count; index++)
        {
            if (ValuesEqual(
                    probe,
                    NormalizeValue(WorkflowMissingValueHelper.RequirePresent(args[index]))))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        left = WorkflowMissingValueHelper.RequirePresent(left);
        right = WorkflowMissingValueHelper.RequirePresent(right);
        if (TryAsNumber(left, out var leftNumber) && TryAsNumber(right, out var rightNumber))
        {
            return leftNumber == rightNumber;
        }

        return string.Equals(AsString(left), AsString(right), StringComparison.Ordinal);
    }

    private static int Count(object? value)
    {
        var normalized = NormalizeValue(value);
        return normalized switch
        {
            null => 0,
            string text => text.Length,
            JsonArray array => array.Count,
            JsonObject obj => obj.Count,
            IEnumerable<object?> enumerable => enumerable.Count(),
            _ => 0
        };
    }

    private static bool Any(object? value)
    {
        var normalized = NormalizeValue(value);
        return normalized switch
        {
            JsonArray array => array.Any(ToBoolean),
            IEnumerable<object?> enumerable => enumerable.Any(ToBoolean),
            _ => ToBoolean(normalized)
        };
    }

    private static bool All(object? value)
    {
        var normalized = NormalizeValue(value);
        return normalized switch
        {
            JsonArray array => array.Count > 0 && array.All(ToBoolean),
            IEnumerable<object?> enumerable => enumerable.Any() && enumerable.All(ToBoolean),
            _ => ToBoolean(normalized)
        };
    }

    private static bool IsEmpty(object? value)
    {
        var normalized = NormalizeValue(value);
        return normalized switch
        {
            null => true,
            string text => text.Length == 0,
            JsonArray array => array.Count == 0,
            JsonObject obj => obj.Count == 0,
            IEnumerable<object?> enumerable => !enumerable.Any(),
            _ => false
        };
    }

    private static string AsString(object? value)
    {
        value = WorkflowMissingValueHelper.RequirePresent(value);
        var normalized = NormalizeValue(value);
        return normalized switch
        {
            null => string.Empty,
            JsonNode node => node.ToJsonString(),
            _ => normalized.ToString() ?? string.Empty
        };
    }

    private static StringComparison ReadStringComparison(
        IReadOnlyList<object?> args,
        int index)
        => args.Count > index && ToBoolean(args[index])
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static object? NormalizeValue(object? value)
    {
        value = WorkflowMissingValueHelper.NormalizeMissingToNull(value);
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<bool>(out var boolean)) return boolean;
            if (jsonValue.TryGetValue<long>(out var integer)) return integer;
            if (jsonValue.TryGetValue<decimal>(out var number)) return number;
            if (jsonValue.TryGetValue<double>(out var floatingPoint)) return floatingPoint;
            if (jsonValue.TryGetValue<string>(out var text)) return text;
            return jsonValue.ToJsonString();
        }

        return value;
    }

    private static bool ToBoolean(object? value)
    {
        var normalized = NormalizeValue(value);
        return normalized switch
        {
            null => false,
            bool boolean => boolean,
            string text => !string.IsNullOrWhiteSpace(text),
            sbyte number => number != 0,
            byte number => number != 0,
            short number => number != 0,
            ushort number => number != 0,
            int number => number != 0,
            uint number => number != 0,
            long number => number != 0,
            ulong number => number != 0,
            float number => Math.Abs(number) > float.Epsilon,
            double number => Math.Abs(number) > double.Epsilon,
            decimal number => number != 0,
            JsonArray array => array.Count > 0,
            JsonObject obj => obj.Count > 0,
            _ => true
        };
    }

    private static bool TryAsNumber(object? value, out decimal number)
    {
        var normalized = NormalizeValue(value);
        switch (normalized)
        {
            case byte typed: number = typed; return true;
            case sbyte typed: number = typed; return true;
            case short typed: number = typed; return true;
            case ushort typed: number = typed; return true;
            case int typed: number = typed; return true;
            case uint typed: number = typed; return true;
            case long typed: number = typed; return true;
            case ulong typed: number = typed; return true;
            case float typed: number = (decimal)typed; return true;
            case double typed: number = (decimal)typed; return true;
            case decimal typed: number = typed; return true;
            case string text when decimal.TryParse(
                text,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var parsed):
                number = parsed;
                return true;
            default:
                number = 0;
                return false;
        }
    }
}

internal sealed class ValueExpressionCompilerParser
{
    private static readonly IReadOnlyDictionary<string, (int Min, int Max)> FunctionArities =
        new Dictionary<string, (int Min, int Max)>(StringComparer.Ordinal)
        {
            ["startsWith"] = (2, 3),
            ["endsWith"] = (2, 3),
            ["contains"] = (2, 3),
            ["matches"] = (2, 3),
            ["toLower"] = (1, 1),
            ["toUpper"] = (1, 1),
            ["trim"] = (1, 1),
            ["exists"] = (1, 1),
            ["isNull"] = (1, 1),
            ["isNotNull"] = (1, 1),
            ["isEmpty"] = (1, 1),
            ["isNotEmpty"] = (1, 1),
            ["in"] = (2, int.MaxValue),
            ["count"] = (1, 1),
            ["any"] = (1, 1),
            ["all"] = (1, 1),
            ["pluck"] = (2, 2),
            ["select"] = (2, 2),
            ["where"] = (2, 2),
            ["compact"] = (1, 1),
            ["distinct"] = (1, 1),
            ["setEquals"] = (2, 2),
            ["isSubset"] = (2, 2),
            ["first"] = (1, 1),
            ["last"] = (1, 1),
            ["maxBy"] = (2, 2),
            ["minBy"] = (2, 2),
            ["orderBy"] = (2, 3),
            ["coalesce"] = (1, int.MaxValue),
            ["defaultIfEmpty"] = (2, 2),
            ["join"] = (2, 2),
            ["split"] = (2, 2),
            ["merge"] = (2, int.MaxValue),
            ["set"] = (3, 3),
            ["append"] = (2, 2),
            ["insert"] = (3, 3),
            ["removeWhere"] = (2, 2),
            ["removeAt"] = (2, 2)
        };

    private readonly string _expression;
    private int _position;

    public ValueExpressionCompilerParser(string expression)
    {
        _expression = expression;
    }

    public CompiledWorkflowValueExpression Compile()
    {
        var root = ParseExpression();
        SkipWhitespace();
        if (!IsEnd())
        {
            throw Error($"Unexpected token at position {_position}.");
        }

        return new CompiledWorkflowValueExpression(_expression, root);
    }

    private ValueExpressionNode ParseExpression()
    {
        var left = ParsePrimary();
        while (true)
        {
            SkipWhitespace();
            if (!TryConsume("+"))
            {
                return left;
            }

            left = new ValueExpressionAddNode(left, ParsePrimary());
        }
    }

    private ValueExpressionNode ParsePrimary()
    {
        SkipWhitespace();
        if (TryConsume("("))
        {
            var nested = ParseExpression();
            Require(")");
            return nested;
        }

        if (TryPeek('{')) return ParseObjectLiteral();
        if (TryPeek('[')) return ParseArrayLiteral();
        if (TryPeek('"') || TryPeek('\'')) return new ValueExpressionLiteralNode(ReadString());
        if (TryPeek('$'))
        {
            var path = ReadJsonPathToken();
            return new ValueExpressionPathNode(path, JsonPath.Compile(path));
        }
        if (TryReadKeyword("true")) return new ValueExpressionLiteralNode(true);
        if (TryReadKeyword("false")) return new ValueExpressionLiteralNode(false);
        if (TryReadKeyword("null")) return new ValueExpressionLiteralNode(null);
        if (TryReadNumber(out var number)) return new ValueExpressionLiteralNode(number);

        var identifier = ReadIdentifier();
        if (!string.IsNullOrWhiteSpace(identifier))
        {
            SkipWhitespace();
            if (!TryConsume("("))
            {
                return new ValueExpressionLiteralNode(identifier);
            }

            var args = IsPredicateFunction(identifier)
                ? ParsePredicateArgumentList()
                : ParseArgumentList();
            ValidateFunction(identifier, args.Count);
            var predicate = IsPredicateFunction(identifier)
                ? WorkflowConditionEvaluator.Compile(
                    ((ValueExpressionLiteralNode)args[1]).Value as string)
                : null;
            return new ValueExpressionFunctionNode(identifier, args, predicate);
        }

        throw Error($"Unexpected token at position {_position}.");
    }

    private ValueExpressionNode ParseObjectLiteral()
    {
        Require("{");
        var properties = new List<KeyValuePair<string, ValueExpressionNode>>();
        SkipWhitespace();
        if (TryConsume("}"))
        {
            return new ValueExpressionObjectNode(properties);
        }

        while (true)
        {
            var key = ReadObjectKey();
            Require(":");
            properties.Add(new KeyValuePair<string, ValueExpressionNode>(key, ParseExpression()));
            SkipWhitespace();
            if (TryConsume("}"))
            {
                return new ValueExpressionObjectNode(properties);
            }

            Require(",");
            SkipWhitespace();
            if (TryPeek('}'))
            {
                throw Error($"Expected object key at position {_position}.");
            }
        }
    }

    private ValueExpressionNode ParseArrayLiteral()
    {
        Require("[");
        var items = new List<ValueExpressionNode>();
        SkipWhitespace();
        if (TryConsume("]"))
        {
            return new ValueExpressionArrayNode(items);
        }

        while (true)
        {
            items.Add(ParseExpression());
            SkipWhitespace();
            if (TryConsume("]"))
            {
                return new ValueExpressionArrayNode(items);
            }

            Require(",");
            SkipWhitespace();
            if (TryPeek(']'))
            {
                throw Error($"Expected array item at position {_position}.");
            }
        }
    }

    private string ReadObjectKey()
    {
        SkipWhitespace();
        if (TryPeek('"') || TryPeek('\''))
        {
            return ReadString();
        }

        var identifier = ReadIdentifier();
        if (!string.IsNullOrWhiteSpace(identifier))
        {
            return identifier;
        }

        throw Error($"Expected object key at position {_position}.");
    }

    private IReadOnlyList<ValueExpressionNode> ParseArgumentList()
    {
        var args = new List<ValueExpressionNode>();
        SkipWhitespace();
        if (TryConsume(")"))
        {
            return args;
        }

        while (true)
        {
            args.Add(ParseExpression());
            SkipWhitespace();
            if (TryConsume(")"))
            {
                return args;
            }

            Require(",");
        }
    }

    private IReadOnlyList<ValueExpressionNode> ParsePredicateArgumentList()
    {
        var args = new List<ValueExpressionNode>();
        SkipWhitespace();
        if (TryConsume(")"))
        {
            return args;
        }

        args.Add(ParseExpression());
        Require(",");
        SkipWhitespace();
        args.Add(new ValueExpressionLiteralNode(
            TryPeek('"') || TryPeek('\'')
                ? ReadString()
                : ReadRawPredicate()));
        Require(")");
        return args;
    }

    private string ReadRawPredicate()
    {
        SkipWhitespace();
        var start = _position;
        var inQuote = false;
        var quote = '\0';
        var bracketDepth = 0;
        var braceDepth = 0;
        var parenthesisDepth = 0;
        while (!IsEnd())
        {
            var character = Peek();
            if (inQuote)
            {
                _position++;
                if (character == '\\' && !IsEnd())
                {
                    _position++;
                    continue;
                }

                if (character == quote) inQuote = false;
                continue;
            }

            if (character is '"' or '\'')
            {
                inQuote = true;
                quote = character;
                _position++;
                continue;
            }

            if (character == '[') bracketDepth++;
            else if (character == ']')
            {
                if (bracketDepth == 0) throw Error($"Unexpected ']' in predicate at position {_position}.");
                bracketDepth--;
            }
            else if (character == '{') braceDepth++;
            else if (character == '}')
            {
                if (braceDepth == 0) throw Error($"Unexpected '}}' in predicate at position {_position}.");
                braceDepth--;
            }
            else if (character == '(') parenthesisDepth++;
            else if (character == ')')
            {
                if (parenthesisDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                {
                    break;
                }

                if (parenthesisDepth > 0) parenthesisDepth--;
            }

            _position++;
        }

        if (inQuote || bracketDepth != 0 || braceDepth != 0 || parenthesisDepth != 0)
        {
            throw Error($"Unterminated predicate at position {_position}.");
        }

        return _expression[start.._position].Trim();
    }

    private void ValidateFunction(string name, int argumentCount)
    {
        if (!FunctionArities.TryGetValue(name, out var arity))
        {
            throw new InvalidOperationException(
                $"Unsupported value function '{name}' in expression '{_expression}'.");
        }

        if (argumentCount < arity.Min || argumentCount > arity.Max)
        {
            var expected = arity.Min == arity.Max
                ? $"exactly {arity.Min}"
                : $"between {arity.Min} and {arity.Max}";
            throw new InvalidOperationException($"Function '{name}' expects {expected} arguments.");
        }
    }

    private string ReadJsonPathToken()
    {
        var result = new StringBuilder();
        var inQuote = false;
        var quote = '\0';
        var bracketDepth = 0;
        var parenthesisDepth = 0;
        while (!IsEnd())
        {
            var character = Peek();
            if (inQuote)
            {
                result.Append(character);
                _position++;
                if (character == '\\' && !IsEnd())
                {
                    result.Append(Peek());
                    _position++;
                    continue;
                }

                if (character == quote) inQuote = false;
                continue;
            }

            if (character is '"' or '\'')
            {
                inQuote = true;
                quote = character;
                result.Append(character);
                _position++;
                continue;
            }

            if (character == '[') bracketDepth++;
            else if (character == ']')
            {
                if (bracketDepth == 0) break;
                bracketDepth--;
            }
            else if (character == '(') parenthesisDepth++;
            else if (character == ')')
            {
                if (parenthesisDepth == 0 && bracketDepth == 0) break;
                if (parenthesisDepth > 0) parenthesisDepth--;
            }
            else if (bracketDepth == 0
                     && parenthesisDepth == 0
                     && (char.IsWhiteSpace(character) || character is ',' or '+'))
            {
                break;
            }

            result.Append(character);
            _position++;
        }

        if (result.Length == 0) throw Error($"Expected JSON path at position {_position}.");
        if (inQuote || bracketDepth != 0 || parenthesisDepth != 0)
        {
            throw Error($"Unterminated JSON path token at position {_position}.");
        }

        return result.ToString();
    }

    private string ReadString()
    {
        var quote = Peek();
        _position++;
        var result = new StringBuilder();
        while (!IsEnd())
        {
            var character = Peek();
            _position++;
            if (character == '\\')
            {
                if (IsEnd()) throw Error("Unterminated escape sequence.");
                var escaped = Peek();
                _position++;
                result.Append(escaped switch
                {
                    '\\' => '\\',
                    '"' => '"',
                    '\'' => '\'',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => escaped
                });
                continue;
            }

            if (character == quote) return result.ToString();
            result.Append(character);
        }

        throw Error("Unterminated string literal.");
    }

    private bool TryReadNumber(out decimal number)
    {
        number = 0;
        SkipWhitespace();
        var start = _position;
        if (TryPeek('-')) _position++;
        var hasDigits = false;
        while (!IsEnd() && char.IsDigit(Peek()))
        {
            hasDigits = true;
            _position++;
        }

        if (!IsEnd() && Peek() == '.')
        {
            _position++;
            while (!IsEnd() && char.IsDigit(Peek()))
            {
                hasDigits = true;
                _position++;
            }
        }

        if (!hasDigits)
        {
            _position = start;
            return false;
        }

        var token = _expression[start.._position];
        if (!decimal.TryParse(token, NumberStyles.Number, CultureInfo.InvariantCulture, out number))
        {
            throw Error($"Invalid numeric value '{token}'.");
        }

        return true;
    }

    private bool TryReadKeyword(string keyword)
    {
        SkipWhitespace();
        if (!_expression.AsSpan(_position).StartsWith(keyword, StringComparison.Ordinal))
        {
            return false;
        }

        var end = _position + keyword.Length;
        if (end < _expression.Length
            && (char.IsLetterOrDigit(_expression[end]) || _expression[end] == '_'))
        {
            return false;
        }

        _position = end;
        return true;
    }

    private string? ReadIdentifier()
    {
        SkipWhitespace();
        var start = _position;
        while (!IsEnd() && (char.IsLetterOrDigit(Peek()) || Peek() is '_' or '-'))
        {
            _position++;
        }

        return start == _position ? null : _expression[start.._position];
    }

    private static bool IsPredicateFunction(string name)
        => name is "where" or "removeWhere";

    private void Require(string token)
    {
        SkipWhitespace();
        if (!TryConsume(token))
        {
            throw Error($"Expected '{token}' at position {_position}.");
        }
    }

    private bool TryConsume(string token)
    {
        SkipWhitespace();
        if (!_expression.AsSpan(_position).StartsWith(token, StringComparison.Ordinal))
        {
            return false;
        }

        _position += token.Length;
        return true;
    }

    private bool TryPeek(char character)
    {
        SkipWhitespace();
        return !IsEnd() && Peek() == character;
    }

    private char Peek() => _expression[_position];

    private void SkipWhitespace()
    {
        while (!IsEnd() && char.IsWhiteSpace(Peek()))
        {
            _position++;
        }
    }

    private bool IsEnd() => _position >= _expression.Length;

    private InvalidOperationException Error(string detail)
        => new($"Invalid value expression '{_expression}'. {detail}");
}
