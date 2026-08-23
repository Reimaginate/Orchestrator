using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Reimaginate.Orchestrator.Common.Helpers;

internal static class WorkflowValueExpressionEvaluator
{
    public static JsonNode? Evaluate(string expression, JsonNode currentNode, JsonObject? environment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        ArgumentNullException.ThrowIfNull(currentNode);

        return Evaluate(expression, new WorkflowEvaluationScope(currentNode, environment));
    }

    internal static JsonNode? Evaluate(string expression, WorkflowEvaluationScope scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        return Compile(expression).Evaluate(scope, allowMissingPaths: false);
    }

    internal static JsonNode? Evaluate(
        CompiledWorkflowValueExpression expression,
        JsonNode currentNode,
        JsonObject? environment = null)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(currentNode);
        return expression.Evaluate(
            new WorkflowEvaluationScope(currentNode, environment),
            allowMissingPaths: false);
    }

    internal static JsonNode? Evaluate(
        CompiledWorkflowValueExpression expression,
        WorkflowEvaluationScope scope,
        bool allowMissingPaths = false)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return expression.Evaluate(scope, allowMissingPaths);
    }

    public static void ValidateSyntax(string expression, JsonNode currentNode, JsonObject? environment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        ArgumentNullException.ThrowIfNull(currentNode);

        _ = Compile(expression).Evaluate(
            new WorkflowEvaluationScope(currentNode, environment),
            allowMissingPaths: true);
    }

    internal static CompiledWorkflowValueExpression Compile(string expression)
        => WorkflowValueExpressionCompiler.Compile(expression);

    private sealed class Parser(string expression, WorkflowEvaluationScope scope, bool allowMissingPaths)
    {
        private readonly string _expression = expression;
        private readonly WorkflowEvaluationScope _scope = scope;
        private readonly bool _allowMissingPaths = allowMissingPaths;
        private int _position;

        public JsonNode? ParseToNode()
        {
            var result = ParseExpression();
            SkipWhitespace();

            if (!IsEnd())
            {
                throw new InvalidOperationException($"Invalid value expression '{_expression}'. Unexpected token at position {_position}.");
            }

            return ToJsonNode(result);
        }

        private object? ParseExpression()
        {
            var left = ParsePrimary();

            while (true)
            {
                SkipWhitespace();
                if (!TryConsume("+"))
                {
                    return left;
                }

                var right = ParsePrimary();
                left = Add(left, right);
            }
        }

        private object? ParsePrimary()
        {
            SkipWhitespace();

            if (TryConsume("("))
            {
                var nested = ParseExpression();
                Require(")");
                return nested;
            }

            if (TryPeek('{'))
            {
                return ParseObjectLiteral();
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
                    var args = IsPredicateFunction(identifier)
                        ? ParsePredicateArgumentList()
                        : ParseArgumentList();
                    return EvaluateFunction(identifier, args);
                }

                return identifier;
            }

            throw new InvalidOperationException($"Invalid value expression '{_expression}'. Unexpected token at position {_position}.");
        }

        private JsonObject ParseObjectLiteral()
        {
            Require("{");
            var obj = new JsonObject();

            SkipWhitespace();
            if (TryConsume("}"))
            {
                return obj;
            }

            while (true)
            {
                var key = ReadObjectKey();
                Require(":");

                var value = ParseExpression();
                obj[key] = ToJsonNode(value);

                SkipWhitespace();
                if (TryConsume("}"))
                {
                    return obj;
                }

                Require(",");
                SkipWhitespace();
                if (TryPeek('}'))
                {
                    throw new InvalidOperationException($"Invalid value expression '{_expression}'. Expected object key at position {_position}.");
                }
            }
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
                var value = ParseExpression();
                items.Add(ToJsonNode(value));

                SkipWhitespace();
                if (TryConsume("]"))
                {
                    return items;
                }

                Require(",");
                SkipWhitespace();
                if (TryPeek(']'))
                {
                    throw new InvalidOperationException($"Invalid value expression '{_expression}'. Expected array item at position {_position}.");
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

            throw new InvalidOperationException($"Invalid value expression '{_expression}'. Expected object key at position {_position}.");
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
                args.Add(ParseExpression());
                SkipWhitespace();

                if (TryConsume(")"))
                {
                    return args;
                }

                Require(",");
            }
        }

        private object? EvaluateFunction(string name, IReadOnlyList<object?> args)
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
                    "where" => WorkflowExpressionFunctions.Where(args, _scope),
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
                    "removeWhere" => WorkflowExpressionFunctions.RemoveWhere(args, _scope),
                    "removeAt" => WorkflowExpressionFunctions.RemoveAt(args),
                    _ => throw new InvalidOperationException($"Unsupported value function '{name}' in expression '{_expression}'.")
                };
            }
            catch (WorkflowMissingValueException) when (_allowMissingPaths)
            {
                return new WorkflowMissingValue("$", _expression);
            }
        }

        private object Add(object? left, object? right)
        {
            if (_allowMissingPaths && (WorkflowMissingValueHelper.IsMissing(left) || WorkflowMissingValueHelper.IsMissing(right)))
            {
                return left is WorkflowMissingValue ? left : right!;
            }

            left = WorkflowMissingValueHelper.RequirePresent(left);
            right = WorkflowMissingValueHelper.RequirePresent(right);

            var normalizedLeft = NormalizeValue(left);
            var normalizedRight = NormalizeValue(right);

            if (TryAsNumber(normalizedLeft, out var leftNumber) && TryAsNumber(normalizedRight, out var rightNumber))
            {
                return leftNumber + rightNumber;
            }

            return $"{AsString(normalizedLeft)}{AsString(normalizedRight)}";
        }

        private static bool EvaluateStartsWith(IReadOnlyList<object?> args)
        {
            EnsureArgCountInRange("startsWith", args, 2, 3);
            var source = AsString(args[0]);
            var prefix = AsString(args[1]);
            var comparison = ReadStringComparison(args, 2);
            return source.StartsWith(prefix, comparison);
        }

        private static bool EvaluateEndsWith(IReadOnlyList<object?> args)
        {
            EnsureArgCountInRange("endsWith", args, 2, 3);
            var source = AsString(args[0]);
            var suffix = AsString(args[1]);
            var comparison = ReadStringComparison(args, 2);
            return source.EndsWith(suffix, comparison);
        }

        private static bool EvaluateContains(IReadOnlyList<object?> args)
        {
            EnsureArgCountInRange("contains", args, 2, 3);
            var source = AsString(args[0]);
            var value = AsString(args[1]);
            var comparison = ReadStringComparison(args, 2);
            return source.Contains(value, comparison);
        }

        private static bool EvaluateMatches(IReadOnlyList<object?> args)
        {
            EnsureArgCountInRange("matches", args, 2, 3);
            var source = AsString(args[0]);
            var pattern = AsString(args[1]);
            var ignoreCase = args.Count >= 3 && ToBoolean(args[2]);
            var options = ignoreCase ? RegexOptions.CultureInvariant | RegexOptions.IgnoreCase : RegexOptions.CultureInvariant;
            return Regex.IsMatch(source, pattern, options);
        }

        private static string EvaluateToLower(IReadOnlyList<object?> args)
        {
            EnsureArgCountInRange("toLower", args, 1, 1);
            return AsString(args[0]).ToLowerInvariant();
        }

        private static string EvaluateToUpper(IReadOnlyList<object?> args)
        {
            EnsureArgCountInRange("toUpper", args, 1, 1);
            return AsString(args[0]).ToUpperInvariant();
        }

        private static string EvaluateTrim(IReadOnlyList<object?> args)
        {
            EnsureArgCountInRange("trim", args, 1, 1);
            return AsString(args[0]).Trim();
        }

        private bool EvaluateExists(IReadOnlyList<object?> args)
        {
            EnsureArgCountInRange("exists", args, 1, 1);
            if (args[0] is string path && path.StartsWith("$", StringComparison.Ordinal))
            {
                using var enumerator = _scope.SelectTokens(path).GetEnumerator();
                return enumerator.MoveNext();
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

        private static bool EvaluateIn(IReadOnlyList<object?> args)
        {
            EnsureArgCountInRange("in", args, 2, int.MaxValue);
            var probe = NormalizeValue(WorkflowMissingValueHelper.RequirePresent(args[0]));

            if (args.Count == 2 && NormalizeValue(WorkflowMissingValueHelper.RequirePresent(args[1])) is JsonArray array)
            {
                return array.Any(item => ValuesEqual(probe, NormalizeValue(item)));
            }

            for (var i = 1; i < args.Count; i++)
            {
                if (ValuesEqual(probe, NormalizeValue(WorkflowMissingValueHelper.RequirePresent(args[i]))))
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

        private object? ResolveJsonPath(string path)
            => _scope.Resolve(path, _expression);

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

        private static StringComparison ReadStringComparison(IReadOnlyList<object?> args, int index)
        {
            var ignoreCase = args.Count > index && ToBoolean(args[index]);
            return ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
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

        private JsonNode? ToJsonNode(object? value)
        {
            if (_allowMissingPaths && WorkflowMissingValueHelper.IsMissing(value))
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

        private string ReadJsonPathToken()
        {
            var builder = new StringBuilder();
            var inQuote = false;
            char quote = '\0';
            var bracketDepth = 0;
            var parenDepth = 0;

            while (!IsEnd())
            {
                var c = Peek();
                if (inQuote)
                {
                    builder.Append(c);
                    _position++;

                    if (c == '\\' && !IsEnd())
                    {
                        builder.Append(Peek());
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
                    builder.Append(c);
                    _position++;
                    continue;
                }

                if (c == '[')
                {
                    bracketDepth++;
                    builder.Append(c);
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
                    builder.Append(c);
                    _position++;
                    continue;
                }

                if (c == '(')
                {
                    parenDepth++;
                    builder.Append(c);
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

                    builder.Append(c);
                    _position++;
                    continue;
                }

                if (bracketDepth == 0 && parenDepth == 0 && (char.IsWhiteSpace(c) || c == ',' || c == '+'))
                {
                    break;
                }

                builder.Append(c);
                _position++;
            }

            if (builder.Length == 0)
            {
                throw new InvalidOperationException($"Invalid value expression '{_expression}'. Expected JSON path at position {_position}.");
            }

            if (inQuote || bracketDepth != 0 || parenDepth != 0)
            {
                throw new InvalidOperationException($"Invalid value expression '{_expression}'. Unterminated JSON path token at position {_position}.");
            }

            return builder.ToString();
        }

        private string ReadString()
        {
            var quote = Peek();
            _position++;
            var builder = new StringBuilder();

            while (!IsEnd())
            {
                var c = Peek();
                _position++;

                if (c == '\\')
                {
                    if (IsEnd())
                    {
                        throw new InvalidOperationException($"Invalid value expression '{_expression}'. Unterminated escape sequence.");
                    }

                    var escaped = Peek();
                    _position++;
                    builder.Append(escaped switch
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
                    return builder.ToString();
                }

                builder.Append(c);
            }

            throw new InvalidOperationException($"Invalid value expression '{_expression}'. Unterminated string literal.");
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
                throw new InvalidOperationException($"Invalid numeric value '{token}' in value expression '{_expression}'.");
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

        private static bool IsPredicateFunction(string name)
            => name is "where" or "removeWhere";

        private List<object?> ParsePredicateArgumentList()
        {
            var args = new List<object?>();
            SkipWhitespace();

            if (TryConsume(")"))
            {
                return args;
            }

            args.Add(ParseExpression());
            Require(",");

            SkipWhitespace();
            args.Add(TryPeek('"') || TryPeek('\'')
                ? ParseExpression()
                : ReadRawPredicate());

            Require(")");
            return args;
        }

        private string ReadRawPredicate()
        {
            SkipWhitespace();
            var start = _position;
            var inQuote = false;
            char quote = '\0';
            var bracketDepth = 0;
            var braceDepth = 0;
            var parenDepth = 0;

            while (!IsEnd())
            {
                var c = Peek();

                if (inQuote)
                {
                    _position++;

                    if (c == '\\' && !IsEnd())
                    {
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
                    _position++;
                    continue;
                }

                if (c == '[')
                {
                    bracketDepth++;
                    _position++;
                    continue;
                }

                if (c == ']')
                {
                    if (bracketDepth == 0)
                    {
                        throw new InvalidOperationException($"Invalid value expression '{_expression}'. Unexpected ']' in predicate at position {_position}.");
                    }

                    bracketDepth--;
                    _position++;
                    continue;
                }

                if (c == '{')
                {
                    braceDepth++;
                    _position++;
                    continue;
                }

                if (c == '}')
                {
                    if (braceDepth == 0)
                    {
                        throw new InvalidOperationException($"Invalid value expression '{_expression}'. Unexpected '}}' in predicate at position {_position}.");
                    }

                    braceDepth--;
                    _position++;
                    continue;
                }

                if (c == '(')
                {
                    parenDepth++;
                    _position++;
                    continue;
                }

                if (c == ')')
                {
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                    {
                        break;
                    }

                    if (parenDepth > 0)
                    {
                        parenDepth--;
                    }

                    _position++;
                    continue;
                }

                _position++;
            }

            if (inQuote || bracketDepth != 0 || braceDepth != 0 || parenDepth != 0)
            {
                throw new InvalidOperationException($"Invalid value expression '{_expression}'. Unterminated predicate at position {_position}.");
            }

            return _expression[start.._position].Trim();
        }

        private void Require(string token)
        {
            SkipWhitespace();
            if (!TryConsume(token))
            {
                throw new InvalidOperationException($"Invalid value expression '{_expression}'. Expected '{token}' at position {_position}.");
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

        private void SkipWhitespace()
        {
            while (!IsEnd() && char.IsWhiteSpace(Peek()))
            {
                _position++;
            }
        }

        private char Peek() => _expression[_position];

        private bool TryPeek(char value) => !IsEnd() && Peek() == value;

        private bool IsEnd() => _position >= _expression.Length;
    }
}
