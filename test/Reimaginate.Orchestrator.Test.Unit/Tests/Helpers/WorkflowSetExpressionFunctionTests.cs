using System.Text.Json.Nodes;
using FluentAssertions;
using Reimaginate.Orchestrator.Common.Helpers;
using Xunit;

namespace Reimaginate.Orchestrator.Test.Unit.Tests.Helpers;

public class WorkflowSetExpressionFunctionTests
{
    [Fact(DisplayName = "setEquals ignores order and duplicates and uses numeric equivalence")]
    public void S00001()
    {
        var payload = new JsonObject
        {
            ["Left"] = new JsonArray(1, 2, 2, "3.0"),
            ["Right"] = new JsonArray(3, 2.0, 1)
        };

        WorkflowConditionEvaluator.Evaluate("setEquals($.Left, $.Right)", payload)
            .Should().BeTrue();
        WorkflowValueExpressionEvaluator.Evaluate("setEquals($.Left, $.Right)", payload)!
            .GetValue<bool>().Should().BeTrue();
    }

    [Fact(DisplayName = "isSubset compares distinct values and detects mismatches")]
    public void S00002()
    {
        var payload = new JsonObject
        {
            ["Left"] = new JsonArray("A", "A"),
            ["Right"] = new JsonArray("A", "B"),
            ["Mismatch"] = new JsonArray("C")
        };

        WorkflowConditionEvaluator.Evaluate("isSubset($.Left, $.Right)", payload)
            .Should().BeTrue();
        WorkflowConditionEvaluator.Evaluate("isSubset($.Mismatch, $.Right)", payload)
            .Should().BeFalse();
    }

    [Fact(DisplayName = "set functions treat null and missing arrays as empty")]
    public void S00003()
    {
        var payload = new JsonObject
        {
            ["NullArray"] = null,
            ["Empty"] = new JsonArray()
        };

        WorkflowConditionEvaluator.Evaluate("setEquals($.NullArray, $.Empty)", payload)
            .Should().BeTrue();
        WorkflowConditionEvaluator.Evaluate("setEquals($.Missing, $.Empty)", payload)
            .Should().BeTrue();
        WorkflowConditionEvaluator.Evaluate("isSubset($.Missing, $.Empty)", payload)
            .Should().BeTrue();
    }

    [Fact(DisplayName = "set functions preserve JSON type identity for structured values")]
    public void S00004()
    {
        var payload = new JsonObject
        {
            ["Left"] = new JsonArray
            {
                new JsonObject { ["id"] = 1, ["status"] = "Active" },
                new JsonArray(1, 2)
            },
            ["Equal"] = new JsonArray
            {
                new JsonArray(1, 2),
                new JsonObject { ["status"] = "Active", ["id"] = 1 }
            },
            ["Different"] = new JsonArray
            {
                new JsonObject { ["id"] = true, ["status"] = "Active" },
                new JsonArray(1, 2)
            },
            ["NestedCollisionLeft"] = new JsonArray
            {
                new JsonArray("a", "b")
            },
            ["NestedCollisionRight"] = new JsonArray
            {
                new JsonArray("a,string:b")
            }
        };

        WorkflowConditionEvaluator.Evaluate("setEquals($.Left, $.Equal)", payload)
            .Should().BeTrue();
        WorkflowConditionEvaluator.Evaluate("setEquals($.Left, $.Different)", payload)
            .Should().BeFalse();
        WorkflowConditionEvaluator.Evaluate(
                "setEquals($.NestedCollisionLeft, $.NestedCollisionRight)",
                payload)
            .Should().BeFalse();
    }

    [Theory(DisplayName = "set functions reject non-array values")]
    [InlineData("setEquals($.Invalid, $.Empty)", "setEquals")]
    [InlineData("isSubset($.Invalid, $.Empty)", "isSubset")]
    public void S00005(string expression, string functionName)
    {
        var payload = new JsonObject
        {
            ["Invalid"] = "not-an-array",
            ["Empty"] = new JsonArray()
        };

        var action = () => WorkflowConditionEvaluator.Evaluate(expression, payload);
        action.Should().Throw<InvalidOperationException>()
            .WithMessage($"*Function '{functionName}' expects array or null arguments*");
    }
}
