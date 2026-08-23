using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Reimaginate.Orchestrator.Common.Helpers;

internal sealed class CompiledWorkflowCondition
{
    private readonly ConditionNode _root;

    internal CompiledWorkflowCondition(string expression, ConditionNode root)
    {
        Expression = expression;
        _root = root;
    }

    public string Expression { get; }

    internal bool Evaluate(WorkflowEvaluationScope scope, bool allowMissingPaths)
        => ConditionRuntime.ToBoolean(_root.Evaluate(new ConditionEvaluationState(scope, Expression, allowMissingPaths)));
}

internal static class WorkflowConditionCompiler
{
    private static readonly ConcurrentDictionary<string, CompiledWorkflowCondition> Cache =
        new(StringComparer.Ordinal);

    public static CompiledWorkflowCondition Compile(string? condition)
    {
        var expression = string.IsNullOrWhiteSpace(condition) ? "true" : condition.Trim();
        return Cache.GetOrAdd(expression, static value => new ConditionCompilerParser(value).Compile());
    }

    internal static int CachedConditionCount => Cache.Count;
}

internal readonly record struct ConditionEvaluationState(
    WorkflowEvaluationScope Scope,
    string Expression,
    bool AllowMissingPaths)
{
    public object? RequirePresent(object? value)
        => AllowMissingPaths ? value : WorkflowMissingValueHelper.RequirePresent(value);
}

internal abstract record ConditionNode
{
    public abstract object? Evaluate(ConditionEvaluationState state);
}

internal sealed record ConditionLiteralNode(object? Value) : ConditionNode
{
    public override object? Evaluate(ConditionEvaluationState state) => Value;
}

internal sealed record ConditionPathNode(string Path, JsonPath.CompiledPath CompiledPath) : ConditionNode
{
    public override object? Evaluate(ConditionEvaluationState state)
        => state.Scope.Resolve(CompiledPath, state.Expression);
}

internal sealed record ConditionArrayNode(IReadOnlyList<ConditionNode> Items) : ConditionNode
{
    public override object? Evaluate(ConditionEvaluationState state)
    {
        var result = new JsonArray();
        foreach (var item in Items)
        {
            var value = state.RequirePresent(item.Evaluate(state));
            result.Add(value is JsonNode node ? node.DeepClone() : JsonSerializer.SerializeToNode(value));
        }

        return result;
    }
}

internal sealed record ConditionNotNode(ConditionNode Operand) : ConditionNode
{
    public override object? Evaluate(ConditionEvaluationState state)
        => !ConditionRuntime.ToBoolean(Operand.Evaluate(state));
}

internal sealed record ConditionAndNode(ConditionNode Left, ConditionNode Right) : ConditionNode
{
    public override object? Evaluate(ConditionEvaluationState state)
    {
        var left = ConditionRuntime.ToBoolean(Left.Evaluate(state));
        return left && ConditionRuntime.ToBoolean(Right.Evaluate(state));
    }
}

internal sealed record ConditionOrNode(ConditionNode Left, ConditionNode Right) : ConditionNode
{
    public override object? Evaluate(ConditionEvaluationState state)
    {
        var left = ConditionRuntime.ToBoolean(Left.Evaluate(state));
        return left || ConditionRuntime.ToBoolean(Right.Evaluate(state));
    }
}

internal sealed record ConditionCompareNode(ConditionNode Left, string? Operator, ConditionNode? Right) : ConditionNode
{
    public override object? Evaluate(ConditionEvaluationState state)
    {
        var left = Left.Evaluate(state);
        return Operator is null
            ? ConditionRuntime.ToBoolean(left)
            : ConditionRuntime.Compare(left, Right!.Evaluate(state), Operator, state);
    }
}

internal sealed record ConditionFunctionNode(string Name, IReadOnlyList<ConditionNode> Arguments) : ConditionNode
{
    public override object? Evaluate(ConditionEvaluationState state)
    {
        var args = new object?[Arguments.Count];
        for (var index = 0; index < Arguments.Count; index++)
        {
            args[index] = Arguments[index].Evaluate(state);
        }

        return ConditionRuntime.EvaluateFunction(Name, args, state);
    }
}

internal static class ConditionRuntime
{
    public static object? EvaluateFunction(
        string name,
        IReadOnlyList<object?> args,
        ConditionEvaluationState state)
    {
        try
        {
            return name switch
            {
                "startsWith" => EvaluateStartsWith(args, state),
                "endsWith" => EvaluateEndsWith(args, state),
                "contains" => EvaluateContains(args, state),
                "matches" => EvaluateMatches(args, state),
                "toLower" => AsString(args[0], state).ToLowerInvariant(),
                "toUpper" => AsString(args[0], state).ToUpperInvariant(),
                "trim" => AsString(args[0], state).Trim(),
                "exists" => EvaluateExists(args, state),
                "isNull" => NormalizeValue(args[0]) is null,
                "isNotNull" => NormalizeValue(args[0]) is not null,
                "isEmpty" => EvaluateIsEmpty(args[0]),
                "isNotEmpty" => !EvaluateIsEmpty(args[0]),
                "in" => EvaluateIn(args, state),
                "count" => EvaluateCount(args[0]),
                "any" => EvaluateAny(args[0]),
                "all" => EvaluateAll(args[0]),
                "pluck" => WorkflowExpressionFunctions.Pluck(args),
                "select" => WorkflowExpressionFunctions.Select(args),
                "where" => WorkflowExpressionFunctions.Where(args, state.Scope),
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
                "removeWhere" => WorkflowExpressionFunctions.RemoveWhere(args, state.Scope),
                "removeAt" => WorkflowExpressionFunctions.RemoveAt(args),
                _ => throw new InvalidOperationException(
                    $"Unsupported condition function '{name}' in expression '{state.Expression}'.")
            };
        }
        catch (WorkflowMissingValueException) when (state.AllowMissingPaths)
        {
            return new WorkflowMissingValue("$", state.Expression);
        }
    }

    public static bool Compare(
        object? left,
        object? right,
        string op,
        ConditionEvaluationState state)
    {
        if (op is "==" or "!="
            && (WorkflowMissingValueHelper.IsMissing(left) || WorkflowMissingValueHelper.IsMissing(right)))
        {
            var other = WorkflowMissingValueHelper.IsMissing(left) ? right : left;
            if (!WorkflowMissingValueHelper.IsMissing(other) && NormalizeValue(other) is null)
            {
                return op == "==";
            }
        }

        left = state.RequirePresent(left);
        right = state.RequirePresent(right);
        var normalizedLeft = NormalizeValue(left);
        var normalizedRight = NormalizeValue(right);

        if (TryAsNumber(normalizedLeft, out var leftNumber)
            && TryAsNumber(normalizedRight, out var rightNumber))
        {
            return op switch
            {
                "==" => leftNumber == rightNumber,
                "!=" => leftNumber != rightNumber,
                "<" => leftNumber < rightNumber,
                "<=" => leftNumber <= rightNumber,
                ">" => leftNumber > rightNumber,
                ">=" => leftNumber >= rightNumber,
                _ => throw new InvalidOperationException($"Unsupported operator '{op}'.")
            };
        }

        if (normalizedLeft is bool leftBool && normalizedRight is bool rightBool)
        {
            return op switch
            {
                "==" => leftBool == rightBool,
                "!=" => leftBool != rightBool,
                _ => throw new InvalidOperationException($"Operator '{op}' cannot be used with boolean values.")
            };
        }

        var leftString = normalizedLeft?.ToString();
        var rightString = normalizedRight?.ToString();
        return op switch
        {
            "==" => string.Equals(leftString, rightString, StringComparison.Ordinal),
            "!=" => !string.Equals(leftString, rightString, StringComparison.Ordinal),
            "<" => string.CompareOrdinal(leftString, rightString) < 0,
            "<=" => string.CompareOrdinal(leftString, rightString) <= 0,
            ">" => string.CompareOrdinal(leftString, rightString) > 0,
            ">=" => string.CompareOrdinal(leftString, rightString) >= 0,
            _ => throw new InvalidOperationException($"Unsupported operator '{op}'.")
        };
    }

    public static bool ToBoolean(object? value)
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

    private static bool EvaluateStartsWith(IReadOnlyList<object?> args, ConditionEvaluationState state)
        => AsString(args[0], state).StartsWith(
            AsString(args[1], state),
            ReadStringComparison(args, 2));

    private static bool EvaluateEndsWith(IReadOnlyList<object?> args, ConditionEvaluationState state)
        => AsString(args[0], state).EndsWith(
            AsString(args[1], state),
            ReadStringComparison(args, 2));

    private static bool EvaluateContains(IReadOnlyList<object?> args, ConditionEvaluationState state)
        => AsString(args[0], state).Contains(
            AsString(args[1], state),
            ReadStringComparison(args, 2));

    private static bool EvaluateMatches(IReadOnlyList<object?> args, ConditionEvaluationState state)
    {
        var options = args.Count >= 3 && ToBoolean(args[2])
            ? RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
            : RegexOptions.CultureInvariant;
        return Regex.IsMatch(AsString(args[0], state), AsString(args[1], state), options);
    }

    private static bool EvaluateExists(IReadOnlyList<object?> args, ConditionEvaluationState state)
    {
        if (args[0] is string path && path.StartsWith("$", StringComparison.Ordinal))
        {
            return state.Scope.SelectToken(path) is not null;
        }

        return NormalizeValue(args[0]) is not null;
    }

    private static bool EvaluateIsEmpty(object? value)
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

    private static bool EvaluateIn(IReadOnlyList<object?> args, ConditionEvaluationState state)
    {
        var probe = NormalizeValue(state.RequirePresent(args[0]));
        if (args.Count == 2 && NormalizeValue(state.RequirePresent(args[1])) is JsonArray array)
        {
            return array.Any(item => ValuesEqual(probe, NormalizeValue(item), state));
        }

        for (var index = 1; index < args.Count; index++)
        {
            if (ValuesEqual(probe, NormalizeValue(state.RequirePresent(args[index])), state))
            {
                return true;
            }
        }

        return false;
    }

    private static int EvaluateCount(object? value)
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

    private static bool EvaluateAny(object? value)
    {
        var normalized = NormalizeValue(value);
        return normalized switch
        {
            JsonArray array => array.Any(ToBoolean),
            IEnumerable<object?> enumerable => enumerable.Any(ToBoolean),
            _ => ToBoolean(normalized)
        };
    }

    private static bool EvaluateAll(object? value)
    {
        var normalized = NormalizeValue(value);
        return normalized switch
        {
            JsonArray array => array.Count > 0 && array.All(ToBoolean),
            IEnumerable<object?> enumerable => enumerable.Any() && enumerable.All(ToBoolean),
            _ => ToBoolean(normalized)
        };
    }

    private static string AsString(object? value, ConditionEvaluationState state)
    {
        value = state.RequirePresent(value);
        return NormalizeValue(value)?.ToString() ?? string.Empty;
    }

    private static StringComparison ReadStringComparison(IReadOnlyList<object?> args, int index)
        => args.Count > index && ToBoolean(args[index])
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static bool ValuesEqual(object? left, object? right, ConditionEvaluationState state)
    {
        left = state.RequirePresent(left);
        right = state.RequirePresent(right);
        if (TryAsNumber(left, out var leftNumber) && TryAsNumber(right, out var rightNumber))
        {
            return leftNumber == rightNumber;
        }

        return string.Equals(left?.ToString(), right?.ToString(), StringComparison.Ordinal);
    }

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

internal sealed class ConditionCompilerParser
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

    public ConditionCompilerParser(string expression)
    {
        _expression = expression;
    }

    public CompiledWorkflowCondition Compile()
    {
        var root = ParseOr();
        SkipWhitespace();
        if (!IsEnd())
        {
            throw Error($"Unexpected token at position {_position}.");
        }

        return new CompiledWorkflowCondition(_expression, root);
    }

    private ConditionNode ParseOr()
    {
        var left = ParseAnd();
        while (true)
        {
            SkipWhitespace();
            if (!TryConsume("||"))
            {
                return left;
            }

            left = new ConditionOrNode(left, ParseAnd());
        }
    }

    private ConditionNode ParseAnd()
    {
        var left = ParseUnary();
        while (true)
        {
            SkipWhitespace();
            if (!TryConsume("&&"))
            {
                return left;
            }

            left = new ConditionAndNode(left, ParseUnary());
        }
    }

    private ConditionNode ParseUnary()
    {
        SkipWhitespace();
        return TryConsume("!")
            ? new ConditionNotNode(ParseUnary())
            : ParseCompare();
    }

    private ConditionNode ParseCompare()
    {
        var left = ParseValue();
        SkipWhitespace();
        var op = ReadOperator();
        return op is null
            ? new ConditionCompareNode(left, null, null)
            : new ConditionCompareNode(left, op, ParseValue());
    }

    private ConditionNode ParseValue()
    {
        SkipWhitespace();
        if (TryConsume("("))
        {
            var nested = ParseOr();
            Require(")");
            return nested;
        }

        if (TryPeek('[')) return ParseArrayLiteral();
        if (TryPeek('"') || TryPeek('\'')) return new ConditionLiteralNode(ReadString());
        if (TryPeek('$'))
        {
            var path = ReadJsonPathToken();
            return new ConditionPathNode(path, JsonPath.Compile(path));
        }
        if (TryReadKeyword("true")) return new ConditionLiteralNode(true);
        if (TryReadKeyword("false")) return new ConditionLiteralNode(false);
        if (TryReadKeyword("null")) return new ConditionLiteralNode(null);
        if (TryReadNumber(out var number)) return new ConditionLiteralNode(number);

        var identifier = ReadIdentifier();
        if (!string.IsNullOrWhiteSpace(identifier))
        {
            SkipWhitespace();
            if (!TryConsume("("))
            {
                return new ConditionLiteralNode(identifier);
            }

            var args = ParseArgumentList();
            ValidateFunction(identifier, args.Count);
            return new ConditionFunctionNode(identifier, args);
        }

        throw Error($"Unexpected token at position {_position}.");
    }

    private ConditionNode ParseArrayLiteral()
    {
        Require("[");
        var items = new List<ConditionNode>();
        SkipWhitespace();
        if (TryConsume("]"))
        {
            return new ConditionArrayNode(items);
        }

        while (true)
        {
            items.Add(ParseValue());
            SkipWhitespace();
            if (TryConsume("]"))
            {
                return new ConditionArrayNode(items);
            }

            Require(",");
        }
    }

    private IReadOnlyList<ConditionNode> ParseArgumentList()
    {
        var args = new List<ConditionNode>();
        SkipWhitespace();
        if (TryConsume(")"))
        {
            return args;
        }

        while (true)
        {
            args.Add(ParseValue());
            SkipWhitespace();
            if (TryConsume(")"))
            {
                return args;
            }

            Require(",");
        }
    }

    private void ValidateFunction(string name, int argumentCount)
    {
        if (!FunctionArities.TryGetValue(name, out var arity))
        {
            throw new InvalidOperationException(
                $"Unsupported condition function '{name}' in expression '{_expression}'.");
        }

        if (argumentCount < arity.Min || argumentCount > arity.Max)
        {
            var expected = arity.Min == arity.Max
                ? $"exactly {arity.Min}"
                : $"between {arity.Min} and {arity.Max}";
            throw new InvalidOperationException($"Function '{name}' expects {expected} arguments.");
        }
    }

    private string? ReadOperator()
    {
        if (TryConsume("==")) return "==";
        if (TryConsume("!=")) return "!=";
        if (TryConsume("<=")) return "<=";
        if (TryConsume(">=")) return ">=";
        if (TryConsume("<")) return "<";
        if (TryConsume(">")) return ">";
        return null;
    }

    private string ReadJsonPathToken()
    {
        var result = new StringBuilder();
        var inQuote = false;
        var quote = '\0';
        var bracketDepth = 0;
        var parenDepth = 0;

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

                if (character == quote)
                {
                    inQuote = false;
                }

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

            if (character == '[')
            {
                bracketDepth++;
                result.Append(character);
                _position++;
                continue;
            }

            if (character == ']')
            {
                if (bracketDepth == 0) break;
                bracketDepth--;
                result.Append(character);
                _position++;
                continue;
            }

            if (character == '(')
            {
                parenDepth++;
                result.Append(character);
                _position++;
                continue;
            }

            if (character == ')')
            {
                if (parenDepth == 0 && bracketDepth == 0) break;
                if (parenDepth > 0) parenDepth--;
                result.Append(character);
                _position++;
                continue;
            }

            if (bracketDepth == 0
                && parenDepth == 0
                && (char.IsWhiteSpace(character) || character == ',' || IsOperatorAtCurrentPosition()))
            {
                break;
            }

            result.Append(character);
            _position++;
        }

        if (result.Length == 0)
        {
            throw Error($"Expected JSON path at position {_position}.");
        }

        if (inQuote || bracketDepth != 0 || parenDepth != 0)
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
                if (IsEnd())
                {
                    throw Error("Unterminated escape sequence.");
                }

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

            if (character == quote)
            {
                return result.ToString();
            }

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

    private string ReadIdentifier()
    {
        SkipWhitespace();
        var start = _position;
        while (!IsEnd() && (char.IsLetterOrDigit(Peek()) || Peek() is '_' or '-'))
        {
            _position++;
        }

        return _expression[start.._position];
    }

    private bool TryReadKeyword(string keyword)
    {
        SkipWhitespace();
        if (!_expression.AsSpan(_position).StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
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

    private bool IsOperatorAtCurrentPosition()
        => StartsWith("==")
           || StartsWith("!=")
           || StartsWith("<=")
           || StartsWith(">=")
           || StartsWith("&&")
           || StartsWith("||")
           || StartsWith("<")
           || StartsWith(">");

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
        if (!StartsWith(token))
        {
            return false;
        }

        _position += token.Length;
        return true;
    }

    private bool StartsWith(string token)
        => _expression.AsSpan(_position).StartsWith(token, StringComparison.Ordinal);

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
        => new($"Invalid condition expression '{_expression}'. {detail}");
}
