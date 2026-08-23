using System.Reflection;
using System.Text.Json.Nodes;
using FluentAssertions;
using Reimaginate.Orchestrator.Common.Helpers;
using Xunit;

namespace Reimaginate.Orchestrator.Test.Unit.Tests.Helpers;

public class WorkflowConditionEvaluatorTests
{
    [Fact(DisplayName = "Condition evaluator treats explicit JSON null as a value in comparisons")]
    public void S00001()
    {
        var payload = new JsonObject
        {
            ["stash"] = new JsonObject
            {
                ["CancellationStatus"] = null
            },
            ["Entity"] = null
        };

        InvokeEvaluate("$.stash.CancellationStatus == \"Resolved\" || $.stash.CancellationStatus == \"resolved\"", payload)
            .Should().BeFalse();

        InvokeEvaluate("$.Entity == null", payload)
            .Should().BeTrue();

        InvokeEvaluate("$.Missing == null", payload)
            .Should().BeTrue();

        InvokeEvaluate("$.Missing != null", payload)
            .Should().BeFalse();
    }

    [Fact(DisplayName = "Condition evaluator still rejects genuinely missing comparison paths")]
    public void S00002()
    {
        var payload = new JsonObject
        {
            ["stash"] = new JsonObject()
        };

        var action = () => InvokeEvaluate("$.stash.CancellationStatus == \"Resolved\"", payload);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*Missing JSON path '$.stash.CancellationStatus'*");
    }

    [Fact(DisplayName = "Condition evaluator short-circuits missing paths guarded by false AND operands")]
    public void S00003()
    {
        var payload = new JsonObject();

        InvokeEvaluate("exists(\"$.output.Success\") && $.output.Success == false", payload)
            .Should().BeFalse();
    }

    [Fact(DisplayName = "Condition evaluator evaluates guarded AND operands when the guard is true")]
    public void S00004()
    {
        var payload = new JsonObject
        {
            ["output"] = new JsonObject
            {
                ["Success"] = false
            }
        };

        InvokeEvaluate("exists(\"$.output.Success\") && $.output.Success == false", payload)
            .Should().BeTrue();
    }

    [Fact(DisplayName = "Condition evaluator short-circuits missing paths guarded by true OR operands")]
    public void S00005()
    {
        var payload = new JsonObject();

        InvokeEvaluate("true || $.Missing == true", payload)
            .Should().BeTrue();
    }

    [Fact(DisplayName = "Condition evaluator still reports unsupported functions in short-circuited operands")]
    public void S00006()
    {
        var payload = new JsonObject();

        var action = () => InvokeEvaluate("true || unsupported($.Missing)", payload);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*Unsupported condition function 'unsupported'*");
    }

    [Fact(DisplayName = "Condition evaluator suppresses missing paths in functions inside short-circuited operands")]
    public void S00007()
    {
        var payload = new JsonObject();

        InvokeEvaluate("true || join($.Missing, \",\") == \"\"", payload)
            .Should().BeTrue();
    }

    [Fact(DisplayName = "Condition evaluator resolves environment overlays without mutating the payload")]
    public void S00008()
    {
        var payload = new JsonObject
        {
            ["env"] = new JsonObject
            {
                ["RequiredStatus"] = "Payload"
            },
            ["Status"] = "Active"
        };
        var original = payload.ToJsonString();
        var environment = new JsonObject
        {
            ["RequiredStatus"] = "Active"
        };

        WorkflowConditionEvaluator.Evaluate(
                "$.Status == $.env.RequiredStatus && $.env['RequiredStatus'] == 'Active'",
                payload,
                environment)
            .Should().BeTrue();

        payload.ToJsonString().Should().Be(original);
        payload["env"]!["RequiredStatus"]!.GetValue<string>().Should().Be("Payload");
    }

    [Fact(DisplayName = "Condition evaluator caches immutable compiled conditions across concurrent evaluations")]
    public void S00009()
    {
        const string expression = "$.Value >= 0 && $.Value < 100";
        var first = WorkflowConditionEvaluator.Compile(expression);
        var second = WorkflowConditionEvaluator.Compile(expression);
        ReferenceEquals(first, second).Should().BeTrue();

        var failures = 0;
        Parallel.For(0, 100, value =>
        {
            var payload = new JsonObject { ["Value"] = value };
            if (!WorkflowConditionEvaluator.Evaluate(first, payload))
            {
                Interlocked.Increment(ref failures);
            }
        });

        failures.Should().Be(0);
    }

    [Fact(DisplayName = "Predicate overlays support object scalar null and nested items without mutating the source")]
    public void S00010()
    {
        var payload = new JsonObject
        {
            ["RequiredStatus"] = "Active",
            ["item"] = new JsonObject { ["Status"] = "Outer" },
            ["Items"] = new JsonArray
            {
                new JsonObject { ["Status"] = "Active" },
                new JsonObject { ["Status"] = "Inactive" }
            },
            ["Scalars"] = new JsonArray("A", "B", null)
        };
        var original = payload.ToJsonString();

        var objectResult = WorkflowValueExpressionEvaluator.Evaluate(
            "where($.Items, $['item']['Status'] == $.RequiredStatus)",
            payload)!.AsArray();
        var scalarResult = WorkflowValueExpressionEvaluator.Evaluate(
            "where($.Scalars, $.item.value == 'B' || $.item.value == null)",
            payload)!.AsArray();

        objectResult.Should().HaveCount(1);
        objectResult[0]!["Status"]!.GetValue<string>().Should().Be("Active");
        scalarResult.Should().HaveCount(2);
        scalarResult[0]!.GetValue<string>().Should().Be("B");
        scalarResult[1].Should().BeNull();
        payload.ToJsonString().Should().Be(original);
        payload["item"]!["Status"]!.GetValue<string>().Should().Be("Outer");

        objectResult[0]!["Status"] = "Changed";
        payload["Items"]![0]!["Status"]!.GetValue<string>().Should().Be("Active");
    }

    private static bool InvokeEvaluate(string expression, JsonObject payload)
    {
        var type = typeof(IWorkflowEnvironmentProvider).Assembly.GetType(
            "Reimaginate.Orchestrator.Common.Helpers.WorkflowConditionEvaluator",
            throwOnError: true)!;
        var method = type.GetMethod(
            "Evaluate",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            [typeof(string), typeof(JsonObject), typeof(JsonObject)]) ?? throw new MissingMethodException(type.FullName, "Evaluate");

        try
        {
            return (bool)method.Invoke(null, [expression, payload, null])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }
}
