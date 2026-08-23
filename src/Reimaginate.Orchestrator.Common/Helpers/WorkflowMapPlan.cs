using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reimaginate.Orchestrator.DSL;

namespace Reimaginate.Orchestrator.Common.Helpers;

internal enum WorkflowMapTransform
{
    Trim,
    Lower,
    Upper,
    ToNumber,
    ToString,
    ToDate
}

internal enum WorkflowMapSelectorSource
{
    Local,
    Environment
}

internal sealed record WorkflowMapSelector(
    string SourcePath,
    WorkflowMapSelectorSource Source,
    JsonPath.CompiledPath Path,
    JsonPath.CompiledPath? OverlayPath = null);

internal sealed class WorkflowMapLiteralPrototype
{
    private readonly JsonNode? prototype;

    private WorkflowMapLiteralPrototype(JsonNode? prototype)
    {
        this.prototype = prototype;
    }

    internal static WorkflowMapLiteralPrototype Compile(object? value)
        => new(value switch
        {
            null => null,
            JsonNode node => node.DeepClone(),
            _ => JsonSerializer.SerializeToNode(value)
        });

    internal JsonNode? Materialize()
        => prototype?.DeepClone();
}

internal sealed record WorkflowMapRulePlan(
    string Target,
    WorkflowMapTargetPath TargetPath,
    WorkflowMapSelector? From,
    CompiledWorkflowValueExpression? Expression,
    WorkflowMapLiteralPrototype? Constant,
    bool HasConstant,
    CompiledWorkflowCondition? When,
    ImmutableArray<WorkflowMapTransform> Transforms,
    WorkflowMapLiteralPrototype? Default,
    bool HasDefault,
    WorkflowMapSelector? Foreach,
    WorkflowMapPlan? NestedMap);

internal sealed record WorkflowMapPlan(
    WorkflowMapSelector Input,
    ImmutableArray<WorkflowMapRulePlan> Rules);

internal static class WorkflowMapPlanCompiler
{
    public static WorkflowMapPlan Compile(MapDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var input = CompileSelector(string.IsNullOrWhiteSpace(definition.Input) ? "$" : definition.Input)!;
        var rules = definition.Rules.Select(CompileRule).ToImmutableArray();
        return new WorkflowMapPlan(input, rules);
    }

    private static WorkflowMapRulePlan CompileRule(MapRuleDefinition rule)
    {
        var targetPath = WorkflowMapTargetPathWriter.Parse(rule.Target);
        if (targetPath.ContainsWildcard)
        {
            throw new InvalidOperationException($"Map rule target '{rule.Target}' cannot use wildcard writes.");
        }

        return new WorkflowMapRulePlan(
            rule.Target,
            targetPath,
            CompileSelector(rule.From),
            string.IsNullOrWhiteSpace(rule.Expression)
                ? null
                : WorkflowValueExpressionEvaluator.Compile(rule.Expression),
            rule.HasConstant ? WorkflowMapLiteralPrototype.Compile(rule.Constant) : null,
            rule.HasConstant,
            string.IsNullOrWhiteSpace(rule.When) ? null : WorkflowConditionEvaluator.Compile(rule.When),
            rule.Transform.Select(ParseTransform).ToImmutableArray(),
            rule.HasDefault ? WorkflowMapLiteralPrototype.Compile(rule.Default) : null,
            rule.HasDefault,
            CompileSelector(rule.Foreach),
            rule.Map is null ? null : Compile(rule.Map));
    }

    private static WorkflowMapSelector? CompileSelector(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (WorkflowEvaluationScope.TryReadRootProperty(path, "env", out var environmentRemainder))
        {
            return new WorkflowMapSelector(
                path,
                WorkflowMapSelectorSource.Environment,
                JsonPath.Compile(path),
                JsonPath.Compile($"${environmentRemainder}"));
        }

        return new WorkflowMapSelector(path, WorkflowMapSelectorSource.Local, JsonPath.Compile(path));
    }

    private static WorkflowMapTransform ParseTransform(string name)
        => name switch
        {
            "trim" => WorkflowMapTransform.Trim,
            "lower" => WorkflowMapTransform.Lower,
            "upper" => WorkflowMapTransform.Upper,
            "toNumber" => WorkflowMapTransform.ToNumber,
            "toString" => WorkflowMapTransform.ToString,
            "toDate" => WorkflowMapTransform.ToDate,
            _ => throw new InvalidOperationException($"Unsupported map transform '{name}'.")
        };

    public static JsonNode? Materialize(WorkflowMapLiteralPrototype? prototype)
        => prototype?.Materialize();
}
