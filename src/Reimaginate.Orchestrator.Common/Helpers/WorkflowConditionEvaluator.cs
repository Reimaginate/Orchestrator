using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Reimaginate.Orchestrator.Common.Helpers;

internal static class WorkflowConditionEvaluator
{
    public static bool Evaluate(string? condition, JsonObject currentObject, JsonObject? environment = null)
    {
        ArgumentNullException.ThrowIfNull(currentObject);

        if (string.IsNullOrWhiteSpace(condition))
        {
            return true;
        }

        return Compile(condition).Evaluate(
            new WorkflowEvaluationScope(currentObject, environment),
            allowMissingPaths: false);
    }

    public static bool Evaluate(
        CompiledWorkflowCondition condition,
        JsonObject currentObject,
        JsonObject? environment = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(currentObject);
        return condition.Evaluate(
            new WorkflowEvaluationScope(currentObject, environment),
            allowMissingPaths: false);
    }

    public static void ValidateSyntax(string? condition, JsonObject currentObject, JsonObject? environment = null)
    {
        ArgumentNullException.ThrowIfNull(currentObject);

        if (string.IsNullOrWhiteSpace(condition))
        {
            return;
        }

        _ = Compile(condition).Evaluate(
            new WorkflowEvaluationScope(currentObject, environment),
            allowMissingPaths: true);
    }

    internal static CompiledWorkflowCondition Compile(string? condition)
        => WorkflowConditionCompiler.Compile(condition);

    internal static bool Evaluate(
        CompiledWorkflowCondition condition,
        WorkflowEvaluationScope scope,
        bool allowMissingPaths = false)
        => condition.Evaluate(scope, allowMissingPaths);

    private sealed class Parser(string expression, JsonObject currentObject, JsonObject? environment, bool allowMissingPaths)
    {
        private readonly string _expression = expression;
        private readonly JsonObject _currentObject = currentObject;
        private readonly bool _allowMissingPaths = allowMissingPaths;
        private int _missingPathSuppressionDepth;
        private int _position;

        public bool Parse()
        {
            var result = ParseOr();
            SkipWhitespace();

            if (!IsEnd())
            {
                throw new InvalidOperationException($"Invalid condition expression '{_expression}'. Unexpected token at position {_position}.");
            }

            return result;
        }

        // orExpr := andExpr ( "||" andExpr )*
        private bool ParseOr()
        {
            var left = ParseAnd();

            while (true)
            {
                SkipWhitespace();
                if (!TryConsume("||"))
                {
                    return left;
                }

                var right = left && !_allowMissingPaths
                    ? ParseWithMissingPathSuppression(ParseAnd)
                    : ParseAnd();
                left = left || right;
            }
        }

        // andExpr := unaryExpr ( "&&" unaryExpr )*
        private bool ParseAnd()
        {
            var left = ParseUnary();

            while (true)
            {
                SkipWhitespace();
                if (!TryConsume("&&"))
                {
                    return left;
                }

                var right = !left && !_allowMissingPaths
                    ? ParseWithMissingPathSuppression(ParseUnary)
                    : ParseUnary();
                left = left && right;
            }
        }

        private bool ParseWithMissingPathSuppression(Func<bool> parse)
        {
            _missingPathSuppressionDepth++;
            try
            {
                return parse();
            }
            finally
            {
                _missingPathSuppressionDepth--;
            }
        }

        // unaryExpr := "!" unaryExpr | cmpExpr
        private bool ParseUnary()
        {
            SkipWhitespace();

            if (TryConsume("!"))
            {
                return !ParseUnary();
            }

            return ParseCompare();
        }

        // cmpExpr := value ( (== != < <= > >=) value )?
        private bool ParseCompare()
        {
            var left = ParseValue();

            SkipWhitespace();

            var op = ReadOperator();
            if (op is null)
            {
                return ToBoolean(left);
            }

            var right = ParseValue();

            return Compare(left, right, op);
        }

        private object? ParseValue()
        {
            SkipWhitespace();

            if (TryConsume("("))
            {
                var nested = ParseOr();
                Require(")");
                return nested;
            }

            if (TryPeek('['))
            {
                return ParseArrayLiteral();
            }

            if (TryPeek('"') || TryPeek('\''))
            {
                return ReadString();
            }

            if (TryPeek('$'))
            {
                return ResolveJsonPath(ReadJsonPathToken());
            }

            if (TryReadKeyword("true")) return true;
            if (TryReadKeyword("false")) return false;
            if (TryReadKeyword("null")) return null;

            if (TryReadNumber(out var number))
            {
                return number;
            }

            var identifier = ReadIdentifier();
            if (!string.IsNullOrWhiteSpace(identifier))
            {
                SkipWhitespace();

                if (TryConsume("("))
                {
                    var args = ParseArgumentList();
                    return EvaluateFunction(identifier, args);
                }

                return identifier;
            }

            throw new InvalidOperationException($"Invalid condition expression '{_expression}'. Unexpected token at position {_position}.");
        }

        private JsonArray ParseArrayLiteral()
        {
            Require("[");
            var items = new JsonArray();

            SkipWhitespace();
            if (TryConsume("]"))
            {
                return items;
            }

            while (true)
            {
                var value = ParseValue();
                value = RequirePresent(value);
                items.Add(value is JsonNode node ? node.DeepClone() : JsonSerializer.SerializeToNode(value));

                SkipWhitespace();
                if (TryConsume("]"))
                {
                    return items;
                }

                Require(",");
            }
        }

        private List<object?> ParseArgumentList()
        {
            var args = new List<object?>();

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

        private object? EvaluateFunction(string name, List<object?> args)
        {
            try
            {
                return name switch
                {
                    "startsWith" => EvaluateStartsWith(args),
                    "endsWith" => EvaluateEndsWith(args),
                    "contains" => EvaluateContains(args),
                    "matches" => EvaluateMatches(args),
                    "toLower" => EvaluateToLower(args),
                    "toUpper" => EvaluateToUpper(args),
                    "trim" => EvaluateTrim(args),
                    "exists" => EvaluateExists(args),
                    "isNull" => EvaluateIsNull(args),
                    "isNotNull" => !EvaluateIsNull(args),
                    "isEmpty" => EvaluateIsEmpty(args),
                    "isNotEmpty" => !EvaluateIsEmpty(args),
                    "in" => EvaluateIn(args),
                    "count" => EvaluateCount(args),
                    "any" => EvaluateAny(args),
                    "all" => EvaluateAll(args),
                    "pluck" => WorkflowExpressionFunctions.Pluck(args),
                    "select" => WorkflowExpressionFunctions.Select(args),
                    "where" => WorkflowExpressionFunctions.Where(args, environment, _currentObject),
                    "compact" => WorkflowExpressionFunctions.Compact(args),
                    "distinct" => WorkflowExpressionFunctions.Distinct(args),
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
                    "removeWhere" => WorkflowExpressionFunctions.RemoveWhere(args, environment, _currentObject),
                    "removeAt" => WorkflowExpressionFunctions.RemoveAt(args),
                    _ => throw new InvalidOperationException($"Unsupported condition function '{name}' in expression '{_expression}'.")
                };
            }
            catch (WorkflowMissingValueException) when (_missingPathSuppressionDepth > 0)
            {
                return null;
            }
        }

        private bool EvaluateStartsWith(IReadOnlyList<object?> args)
        {
            EnsureArgCountInRange("startsWith", args, 2, 3);
            var source = AsString(args[0]) ?? string.Empty;
            var prefix = AsString(args[1]) ?? string.Empty;
            var comparison = ReadStringComparison(args, 2);
            return source.StartsWith(prefix, comparison);
        }

        private bool EvaluateEndsWith(IReadOnlyList<object?> args)
        {
            EnsureArgCountInRange("endsWith", args, 2, 3);
            var source = AsString(args[0]) ?? string.Empty;
            var suffix = AsString(args[1]) ?? string.Empty;
            var comparison = ReadStringComparison(args, 2);
            return source.EndsWith(suffix, comparison);
        }

        private bool EvaluateContains(IReadOnlyList<object?> args)
        {
            EnsureArgCountInRange("contains", args, 2, 3);
            var source = AsString(args[0]) ?? string.Empty;
            var value = AsString(args[1]) ?? string.Empty;
            var comparison = ReadStringComparison(args, 2);
            return source.Contains(value, comparison);
        }

        private bool EvaluateMatches(IReadOnlyList<object?> args)
        {
            EnsureArgCountInRange("matches", args, 2, 3);
            var source = AsString(args[0]) ?? string.Empty;
            var pattern = AsString(args[1]) ?? string.Empty;
            var ignoreCase = args.Count >= 3 && ToBoolean(args[2]);
            var options = ignoreCase ? RegexOptions.CultureInvariant | RegexOptions.IgnoreCase : RegexOptions.CultureInvariant;
            return Regex.IsMatch(source, pattern, options);
        }

        private string EvaluateToLower(IReadOnlyList<object?> args)
        {
            EnsureArgCountInRange("toLower", args, 1, 1);
            return (AsString(args[0]) ?? string.Empty).ToLowerInvariant();
        }

        private string EvaluateToUpper(IReadOnlyList<object?> args)
        {
            EnsureArgCountInRange("toUpper", args, 1, 1);
            return (AsString(args[0]) ?? string.Empty).ToUpperInvariant();
        }

        private string EvaluateTrim(IReadOnlyList<object?> args)
        {
            EnsureArgCountInRange("trim", args, 1, 1);
            return (AsString(args[0]) ?? string.Empty).Trim();
        }

        private bool EvaluateExists(IReadOnlyList<object?> args)
        {
            EnsureArgCountInRange("exists", args, 1, 1);

            if (args[0] is string path && path.StartsWith("$", StringComparison.Ordinal))
            {
                return JsonPath.SelectToken(_currentObject, path) is not null;
            }

            return NormalizeValue(args[0]) is not null;
        }

        private static bool EvaluateIsNull(IReadOnlyList<object?> args)
        {
            EnsureArgCountInRange("isNull", args, 1, 1);
            return NormalizeValue(args[0]) is null;
        }

        private static bool EvaluateIsEmpty(IReadOnlyList<object?> args)
        {
            EnsureArgCountInRange("isEmpty", args, 1, 1);
            var normalized = NormalizeValue(args[0]);

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

        private bool EvaluateIn(IReadOnlyList<object?> args)
        {
            EnsureArgCountInRange("in", args, 2, int.MaxValue);
            var probe = NormalizeValue(RequirePresent(args[0]));

            if (args.Count == 2 && NormalizeValue(RequirePresent(args[1])) is JsonArray array)
            {
                return array.Any(item => ValuesEqual(probe, NormalizeValue(item)));
            }

            for (var i = 1; i < args.Count; i++)
            {
                if (ValuesEqual(probe, NormalizeValue(RequirePresent(args[i]))))
                {
                    return true;
                }
            }

            return false;
        }

        private static int EvaluateCount(IReadOnlyList<object?> args)
        {
            EnsureArgCountInRange("count", args, 1, 1);
            var normalized = NormalizeValue(args[0]);

            return normalized switch
            {
                null => 0,
                string s => s.Length,
                JsonArray array => array.Count,
                JsonObject obj => obj.Count,
                IEnumerable<object?> enumerable => enumerable.Count(),
                _ => 0
            };
        }

        private static bool EvaluateAny(IReadOnlyList<object?> args)
        {
            EnsureArgCountInRange("any", args, 1, 1);
            var normalized = NormalizeValue(args[0]);

            return normalized switch
            {
                JsonArray array => array.Any(ToBoolean),
                IEnumerable<object?> enumerable => enumerable.Any(ToBoolean),
                _ => ToBoolean(normalized)
            };
        }

        private static bool EvaluateAll(IReadOnlyList<object?> args)
        {
            EnsureArgCountInRange("all", args, 1, 1);
            var normalized = NormalizeValue(args[0]);

            return normalized switch
            {
                JsonArray array => array.Count > 0 && array.All(ToBoolean),
                IEnumerable<object?> enumerable => enumerable.Any() && enumerable.All(ToBoolean),
                _ => ToBoolean(normalized)
            };
        }

        private static void EnsureArgCountInRange(string functionName, IReadOnlyList<object?> args, int min, int max)
        {
            if (args.Count < min || args.Count > max)
            {
                var expected = min == max ? $"exactly {min}" : $"between {min} and {max}";
                throw new InvalidOperationException($"Function '{functionName}' expects {expected} arguments.");
            }
        }

        private string? AsString(object? value)
        {
            value = RequirePresent(value);
            var normalized = NormalizeValue(value);
            return normalized?.ToString();
        }

        private static StringComparison ReadStringComparison(IReadOnlyList<object?> args, int index)
        {
            var ignoreCase = args.Count > index && ToBoolean(args[index]);
            return ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        }

        private object? ResolveJsonPath(string path)
        {
            using var tokens = JsonPath.SelectTokens(_currentObject, path).GetEnumerator();
            if (!tokens.MoveNext())
            {
                return new WorkflowMissingValue(path, _expression);
            }

            return tokens.Current;
        }

        private bool Compare(object? left, object? right, string op)
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

            left = RequirePresent(left);
            right = RequirePresent(right);

            var normalizedLeft = NormalizeValue(left);
            var normalizedRight = NormalizeValue(right);

            if (TryAsNumber(normalizedLeft, out var leftNumber) && TryAsNumber(normalizedRight, out var rightNumber))
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

        private bool ValuesEqual(object? left, object? right)
        {
            left = RequirePresent(left);
            right = RequirePresent(right);

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
                if (jsonValue.TryGetValue<bool>(out var b)) return b;
                if (jsonValue.TryGetValue<long>(out var l)) return l;
                if (jsonValue.TryGetValue<decimal>(out var d)) return d;
                if (jsonValue.TryGetValue<double>(out var dbl)) return dbl;
                if (jsonValue.TryGetValue<string>(out var s)) return s;
                return jsonValue.ToJsonString();
            }

            return value;
        }

        private object? RequirePresent(object? value)
            => _allowMissingPaths || _missingPathSuppressionDepth > 0 ? value : WorkflowMissingValueHelper.RequirePresent(value);

        private static bool ToBoolean(object? value)
        {
            var normalized = NormalizeValue(value);

            return normalized switch
            {
                null => false,
                bool b => b,
                string s => !string.IsNullOrWhiteSpace(s),
                sbyte i => i != 0,
                byte i => i != 0,
                short i => i != 0,
                ushort i => i != 0,
                int i => i != 0,
                uint i => i != 0,
                long i => i != 0,
                ulong i => i != 0,
                float f => Math.Abs(f) > float.Epsilon,
                double d => Math.Abs(d) > double.Epsilon,
                decimal m => m != 0,
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
                case byte b: number = b; return true;
                case sbyte sb: number = sb; return true;
                case short s: number = s; return true;
                case ushort us: number = us; return true;
                case int i: number = i; return true;
                case uint ui: number = ui; return true;
                case long l: number = l; return true;
                case ulong ul: number = ul; return true;
                case float f: number = (decimal)f; return true;
                case double d: number = (decimal)d; return true;
                case decimal m: number = m; return true;
                case string str when decimal.TryParse(str, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed):
                    number = parsed;
                    return true;
                default:
                    number = 0;
                    return false;
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
            var sb = new StringBuilder();
            var inQuote = false;
            char quote = '\0';
            var bracketDepth = 0;
            var parenDepth = 0;

            while (!IsEnd())
            {
                var c = Peek();

                if (inQuote)
                {
                    sb.Append(c);
                    _position++;

                    if (c == '\\' && !IsEnd())
                    {
                        sb.Append(Peek());
                        _position++;
                        continue;
                    }

                    if (c == quote)
                    {
                        inQuote = false;
                    }

                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    inQuote = true;
                    quote = c;
                    sb.Append(c);
                    _position++;
                    continue;
                }

                if (c == '[')
                {
                    bracketDepth++;
                    sb.Append(c);
                    _position++;
                    continue;
                }

                if (c == ']')
                {
                    if (bracketDepth == 0)
                    {
                        break;
                    }

                    bracketDepth--;
                    sb.Append(c);
                    _position++;
                    continue;
                }

                if (c == '(')
                {
                    parenDepth++;
                    sb.Append(c);
                    _position++;
                    continue;
                }

                if (c == ')')
                {
                    if (parenDepth == 0 && bracketDepth == 0)
                    {
                        break;
                    }

                    if (parenDepth > 0)
                    {
                        parenDepth--;
                    }

                    sb.Append(c);
                    _position++;
                    continue;
                }

                if (bracketDepth == 0 && parenDepth == 0 && (char.IsWhiteSpace(c) || c == ','))
                {
                    break;
                }

                if (bracketDepth == 0 && parenDepth == 0 && IsOperatorAtCurrentPosition())
                {
                    break;
                }

                sb.Append(c);
                _position++;
            }

            if (sb.Length == 0)
            {
                throw new InvalidOperationException($"Invalid condition expression '{_expression}'. Expected JSON path at position {_position}.");
            }

            if (inQuote || bracketDepth != 0 || parenDepth != 0)
            {
                throw new InvalidOperationException($"Invalid condition expression '{_expression}'. Unterminated JSON path token at position {_position}.");
            }

            return sb.ToString();
        }

        private string ReadString()
        {
            var quote = Peek();
            _position++;

            var sb = new StringBuilder();

            while (!IsEnd())
            {
                var c = Peek();
                _position++;

                if (c == '\\')
                {
                    if (IsEnd())
                    {
                        throw new InvalidOperationException($"Invalid condition expression '{_expression}'. Unterminated escape sequence.");
                    }

                    var escaped = Peek();
                    _position++;

                    sb.Append(escaped switch
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

                if (c == quote)
                {
                    return sb.ToString();
                }

                sb.Append(c);
            }

            throw new InvalidOperationException($"Invalid condition expression '{_expression}'. Unterminated string literal.");
        }

        private bool TryReadNumber(out decimal number)
        {
            number = 0;
            SkipWhitespace();

            var start = _position;

            if (TryPeek('-'))
            {
                _position++;
            }

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
                throw new InvalidOperationException($"Invalid numeric value '{token}' in condition expression '{_expression}'.");
            }

            return true;
        }

        private bool TryReadKeyword(string keyword)
        {
            SkipWhitespace();
            var remaining = _expression.AsSpan(_position);
            if (!remaining.StartsWith(keyword, StringComparison.Ordinal))
            {
                return false;
            }

            var end = _position + keyword.Length;
            if (end < _expression.Length && (char.IsLetterOrDigit(_expression[end]) || _expression[end] == '_'))
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

            while (!IsEnd())
            {
                var c = Peek();
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-')
                {
                    _position++;
                    continue;
                }

                break;
            }

            return start == _position ? null : _expression[start.._position];
        }

        private void Require(string token)
        {
            SkipWhitespace();
            if (!TryConsume(token))
            {
                throw new InvalidOperationException($"Invalid condition expression '{_expression}'. Expected '{token}' at position {_position}.");
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

        private bool IsOperatorAtCurrentPosition()
        {
            var span = _expression.AsSpan(_position);
            return span.StartsWith("==", StringComparison.Ordinal)
                || span.StartsWith("!=", StringComparison.Ordinal)
                || span.StartsWith("<=", StringComparison.Ordinal)
                || span.StartsWith(">=", StringComparison.Ordinal)
                || span.StartsWith("&&", StringComparison.Ordinal)
                || span.StartsWith("||", StringComparison.Ordinal)
                || span.StartsWith("<", StringComparison.Ordinal)
                || span.StartsWith(">", StringComparison.Ordinal)
                || span.StartsWith("!", StringComparison.Ordinal);
        }

        private void SkipWhitespace()
        {
            while (!IsEnd() && char.IsWhiteSpace(Peek()))
            {
                _position++;
            }
        }

        private char Peek() => _expression[_position];

        private bool TryPeek(char c) => !IsEnd() && Peek() == c;

        private bool IsEnd() => _position >= _expression.Length;
    }
}
