using System.Text.Json.Nodes;
using FluentAssertions;
using Reimaginate.Orchestrator.Common.Helpers;
using Xunit;

namespace Reimaginate.Orchestrator.Test.Unit.Tests.Helpers;

public class WorkflowEvaluationScopeTests
{
    [Fact(DisplayName = "Fallback overlay supplies missing object leaves with dot and bracket paths")]
    public void S00001()
    {
        var primary = new JsonObject
        {
            ["profile"] = new JsonObject
            {
                ["name"] = "Primary"
            }
        };
        var fallback = new JsonObject
        {
            ["profile"] = new JsonObject
            {
                ["name"] = "Fallback",
                ["region"] = "AU"
            }
        };
        var scope = new WorkflowEvaluationScope(primary, Fallback: fallback);

        scope.Resolve("$.profile.name", "test").Should().Be(primary["profile"]!["name"]);
        scope.Resolve("$['profile']['region']", "test")
            .Should().Be(fallback["profile"]!["region"]);
    }

    [Fact(DisplayName = "Primary null scalar and array values block fallback subtrees")]
    public void S00002()
    {
        var primary = new JsonObject
        {
            ["nullValue"] = null,
            ["scalarValue"] = "primary",
            ["arrayValue"] = new JsonArray(1)
        };
        var fallback = new JsonObject
        {
            ["nullValue"] = new JsonObject { ["nested"] = "fallback" },
            ["scalarValue"] = new JsonObject { ["nested"] = "fallback" },
            ["arrayValue"] = new JsonObject { ["nested"] = "fallback" }
        };
        var scope = new WorkflowEvaluationScope(primary, Fallback: fallback);

        scope.SelectTokens("$.nullValue.nested").Should().BeEmpty();
        scope.SelectTokens("$.scalarValue.nested").Should().BeEmpty();
        scope.SelectTokens("$.arrayValue.nested").Should().BeEmpty();
        scope.Resolve("$.nullValue", "test").Should().BeNull();
    }

    [Fact(DisplayName = "Fallback traversal is read-only and supports merged wildcard object leaves")]
    public void S00003()
    {
        var primary = new JsonObject
        {
            ["values"] = new JsonObject
            {
                ["overlap"] = 3,
                ["primary"] = 1
            }
        };
        var fallback = new JsonObject
        {
            ["values"] = new JsonObject
            {
                ["fallback"] = 2,
                ["overlap"] = 0
            }
        };
        var primaryBefore = primary.ToJsonString();
        var fallbackBefore = fallback.ToJsonString();
        var scope = new WorkflowEvaluationScope(primary, Fallback: fallback);

        scope.SelectTokens("$.values.*")
            .Select(node => node!.GetValue<int>())
            .Should().Equal(2, 3, 1);
        primary.ToJsonString().Should().Be(primaryBefore);
        fallback.ToJsonString().Should().Be(fallbackBefore);
    }
}
