using System.Text.Json.Nodes;
using FluentAssertions;
using Reimaginate.Orchestrator.Common.Helpers;
using Xunit;

namespace Reimaginate.Orchestrator.Test.Unit.Tests.Helpers;

public class WorkflowValueExpressionEvaluatorTests
{
    [Fact(DisplayName = "Value expression compiler caches immutable plans by normalized text")]
    public void S00001()
    {
        var first = WorkflowValueExpressionEvaluator.Compile(
            " { Label: $.Name + '-' + $.env.Suffix } ");
        var second = WorkflowValueExpressionEvaluator.Compile(
            "{ Label: $.Name + '-' + $.env.Suffix }");

        first.Should().BeSameAs(second);
    }

    [Fact(DisplayName = "Compiled value expressions preserve literals functions overlays predicates and detached outputs")]
    public void S00002()
    {
        const string expression =
            "{ Label: $.Name + '-' + $.env.Suffix, "
            + "ActiveIds: pluck(where($.Items, $.item.Active == true), 'Id'), "
            + "Inactive: removeWhere($.Items, \"$.item.Active == true\"), "
            + "Flags: [true, $.Count + 1] }";
        var source = new JsonObject
        {
            ["Name"] = "Page",
            ["Count"] = 1,
            ["Items"] = new JsonArray(
                new JsonObject { ["Id"] = "A", ["Active"] = true },
                new JsonObject { ["Id"] = "B", ["Active"] = false })
        };
        var environment = new JsonObject { ["Suffix"] = "AU" };
        var sourceBefore = source.ToJsonString();
        var compiled = WorkflowValueExpressionEvaluator.Compile(expression);

        var direct = WorkflowValueExpressionEvaluator.Evaluate(expression, source, environment);
        var planned = WorkflowValueExpressionEvaluator.Evaluate(compiled, source, environment);

        planned!.ToJsonString().Should().Be(direct!.ToJsonString());
        planned["Label"]!.GetValue<string>().Should().Be("Page-AU");
        planned["ActiveIds"]!.AsArray().Select(node => node!.GetValue<string>())
            .Should().Equal("A");
        planned["Inactive"]!.AsArray().Should().HaveCount(1);
        planned["Flags"]![1]!.GetValue<decimal>().Should().Be(2);
        source.ToJsonString().Should().Be(sourceBefore);

        planned["Inactive"]![0]!["Id"] = "Changed";
        source["Items"]![1]!["Id"]!.GetValue<string>().Should().Be("B");
    }

    [Fact(DisplayName = "Compiled value expression plans are safe across concurrent invocation-local scopes")]
    public async Task S00003()
    {
        var compiled = WorkflowValueExpressionEvaluator.Compile(
            "{ Value: $.Value + $.env.Offset, Selected: where($.Items, $.item.value == $.Value) }");

        var outputs = await Task.WhenAll(Enumerable.Range(0, 64).Select(value => Task.Run(() =>
            WorkflowValueExpressionEvaluator.Evaluate(
                compiled,
                new JsonObject
                {
                    ["Value"] = value,
                    ["Items"] = new JsonArray(value - 1, value, value + 1)
                },
                new JsonObject { ["Offset"] = 100 }))));

        outputs.Select(output => output!["Value"]!.GetValue<decimal>())
            .Should().Equal(Enumerable.Range(0, 64).Select(value => (decimal)value + 100));
        outputs.Should().OnlyContain(output => output!["Selected"]!.AsArray().Count == 1);
    }

    [Fact(DisplayName = "Compiled value expression validation remains strict for missing concrete values")]
    public void S00004()
    {
        var compiled = WorkflowValueExpressionEvaluator.Compile("$.Missing + 1");
        var action = () => WorkflowValueExpressionEvaluator.Evaluate(
            compiled,
            new JsonObject());

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*Missing JSON path '$.Missing'*");
    }
}
