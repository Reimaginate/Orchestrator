using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reimaginate.Orchestrator.DSL;

namespace Reimaginate.Orchestrator.Common.Helpers;

internal static class WorkflowMapRuntime
{
    public static JsonObject Execute(MapDefinition definition, JsonNode? sourceContext, JsonObject? environment = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Execute(WorkflowMapPlanCompiler.Compile(definition), sourceContext, environment);
    }

    internal static JsonObject Execute(WorkflowMapPlan plan, JsonNode? sourceContext, JsonObject? environment = null)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var localContext = ResolveInputContext(sourceContext, plan.Input, environment);
        var output = new JsonObject();

        foreach (var rule in plan.Rules)
        {
            ApplyRule(output, localContext, rule, environment);
        }

        return output;
    }

    private static void ApplyRule(JsonObject output, JsonNode? localContext, WorkflowMapRulePlan rule, JsonObject? environment)
    {
        if (!ShouldApplyRule(rule, localContext, environment))
        {
            return;
        }

        var (hasValue, value, ownsValue) = ResolveRuleValue(localContext, rule, environment);
        if (rule.Transforms.Length > 0 && value is not null)
        {
            foreach (var transform in rule.Transforms)
            {
                value = ApplyTransform(transform, value);
            }

            ownsValue = true;
        }

        if ((!hasValue || value is null) && rule.HasDefault)
        {
            value = WorkflowMapPlanCompiler.Materialize(rule.Default);
            hasValue = true;
            ownsValue = true;
        }

        if (!hasValue)
        {
            return;
        }

        if (ownsValue)
        {
            WorkflowMapTargetPathWriter.WriteOwned(output, rule.TargetPath, value);
        }
        else
        {
            WorkflowMapTargetPathWriter.Write(output, rule.TargetPath, value);
        }
    }

    private static bool ShouldApplyRule(WorkflowMapRulePlan rule, JsonNode? localContext, JsonObject? environment)
    {
        if (rule.When is null)
        {
            return true;
        }

        if (localContext is not JsonObject objectContext)
        {
            throw new InvalidOperationException($"Map rule target '{rule.Target}' uses 'when' but the current source context is not an object.");
        }

        return WorkflowConditionEvaluator.Evaluate(rule.When, objectContext, environment);
    }

    private static (bool HasValue, JsonNode? Value, bool OwnsValue) ResolveRuleValue(JsonNode? localContext, WorkflowMapRulePlan rule, JsonObject? environment)
    {
        if (rule.Foreach is not null)
        {
            if (rule.NestedMap is null)
            {
                return (false, null, false);
            }

            var (hasMatch, collectionNode) = TryResolveSelector(localContext, rule.Foreach, environment);
            if (!hasMatch || collectionNode is null)
            {
                return (false, null, false);
            }

            if (collectionNode is not JsonArray array)
            {
                throw new InvalidOperationException($"Map rule target '{rule.Target}' expects 'foreach' to resolve to an array.");
            }

            var mappedItems = new JsonArray();
            foreach (var item in array)
            {
                mappedItems.Add(Execute(rule.NestedMap, item, environment));
            }

            return (true, mappedItems, true);
        }

        if (rule.NestedMap is not null)
        {
            return (true, Execute(rule.NestedMap, localContext, environment), true);
        }

        if (rule.From is not null)
        {
            var (hasMatch, selected) = TryResolveSelector(localContext, rule.From, environment);
            return (hasMatch, selected, false);
        }

        if (rule.Expression is not null)
        {
            var expressionContext = localContext ?? new JsonObject();
            return (
                true,
                WorkflowValueExpressionEvaluator.Evaluate(
                    rule.Expression,
                    expressionContext,
                    environment),
                true);
        }

        if (rule.HasConstant)
        {
            return (true, WorkflowMapPlanCompiler.Materialize(rule.Constant), true);
        }

        return (false, null, false);
    }

    private static JsonNode? ResolveInputContext(JsonNode? sourceContext, WorkflowMapSelector input, JsonObject? environment)
    {
        var (hasMatch, value) = TryResolveSelector(sourceContext, input, environment);
        return hasMatch ? value : null;
    }

    private static (bool HasMatch, JsonNode? Value) TryResolveSelector(
        JsonNode? sourceContext,
        WorkflowMapSelector selector,
        JsonObject? environment = null)
    {
        var path = selector.Path;
        if (selector.Source == WorkflowMapSelectorSource.Environment && environment is not null)
        {
            sourceContext = environment;
            path = selector.OverlayPath
                ?? throw new InvalidOperationException($"Environment map selector '{selector.SourcePath}' does not have a compiled overlay path.");
        }

        if (sourceContext is null)
        {
            return (false, null);
        }

        using var enumerator = path.SelectTokens(sourceContext).GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return (false, null);
        }

        // Map inputs are read-only. The target writer clones the selected value once when
        // it attaches it to the result, avoiding a clone here and another clone on write.
        return (true, enumerator.Current);
    }

    private static JsonNode ApplyTransform(WorkflowMapTransform transform, JsonNode value)
    {
        return transform switch
        {
            WorkflowMapTransform.Trim => JsonValue.Create(ReadStringValue(value).Trim())!,
            WorkflowMapTransform.Lower => JsonValue.Create(ReadStringValue(value).ToLowerInvariant())!,
            WorkflowMapTransform.Upper => JsonValue.Create(ReadStringValue(value).ToUpperInvariant())!,
            WorkflowMapTransform.ToNumber => JsonSerializer.SerializeToNode(ReadNumberValue(value))!,
            WorkflowMapTransform.ToString => JsonValue.Create(ReadStringValue(value))!,
            WorkflowMapTransform.ToDate => JsonValue.Create(ReadDateValue(value).ToString("O"))!,
            _ => throw new InvalidOperationException($"Unsupported map transform '{transform}'.")
        };
    }

    private static string ReadStringValue(JsonNode value)
    {
        return value switch
        {
            JsonValue jsonValue when jsonValue.TryGetValue<string>(out var text) => text,
            JsonValue jsonValue when jsonValue.TryGetValue<bool>(out var booleanValue) => booleanValue.ToString(),
            JsonValue jsonValue when jsonValue.TryGetValue<long>(out var integerValue) => integerValue.ToString(CultureInfo.InvariantCulture),
            JsonValue jsonValue when jsonValue.TryGetValue<decimal>(out var decimalValue) => decimalValue.ToString(CultureInfo.InvariantCulture),
            JsonValue jsonValue when jsonValue.TryGetValue<double>(out var doubleValue) => doubleValue.ToString(CultureInfo.InvariantCulture),
            _ => value.ToJsonString()
        };
    }

    private static decimal ReadNumberValue(JsonNode value)
    {
        return value switch
        {
            JsonValue jsonValue when jsonValue.TryGetValue<decimal>(out var decimalValue) => decimalValue,
            JsonValue jsonValue when jsonValue.TryGetValue<long>(out var longValue) => longValue,
            JsonValue jsonValue when jsonValue.TryGetValue<double>(out var doubleValue) => (decimal)doubleValue,
            JsonValue jsonValue when jsonValue.TryGetValue<string>(out var text) &&
                                     decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => throw new InvalidOperationException($"Value '{value.ToJsonString()}' cannot be converted to a number.")
        };
    }

    private static DateTimeOffset ReadDateValue(JsonNode value)
    {
        return value switch
        {
            JsonValue jsonValue when jsonValue.TryGetValue<DateTimeOffset>(out var dateTimeOffset) => dateTimeOffset,
            JsonValue jsonValue when jsonValue.TryGetValue<DateTime>(out var dateTime) => new DateTimeOffset(dateTime),
            JsonValue jsonValue when jsonValue.TryGetValue<string>(out var text) &&
                                     DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) => parsed,
            _ => throw new InvalidOperationException($"Value '{value.ToJsonString()}' cannot be converted to a date.")
        };
    }

}
