using System.Text.Json.Nodes;
using FluentAssertions;
using Reimaginate.Orchestrator.Common.Helpers;
using Xunit;

namespace Reimaginate.Orchestrator.Test.Unit.Tests.Helpers;

public class WorkflowStashHelperTests
{
    [Fact(DisplayName = "Owned stash merge skips overwritten source values and moves resolved values")]
    public void S00001()
    {
        var sourceLargeArray = new JsonArray(
            Enumerable.Range(0, 10_000)
                .Select(value => (JsonNode?)JsonValue.Create(value))
                .ToArray());
        var sourceUnrelated = new JsonObject
        {
            ["Name"] = "source"
        };
        var source = new JsonObject
        {
            ["stash"] = new JsonObject
            {
                ["Batch"] = sourceLargeArray,
                ["Unrelated"] = sourceUnrelated
            }
        };
        var sourceBefore = source.ToJsonString();
        var ownedReplacement = new JsonArray("replacement");
        var resolvedValues = new JsonObject
        {
            ["Batch"] = ownedReplacement
        };
        var result = new JsonObject
        {
            ["Result"] = true
        };

        var merged = WorkflowStashHelper.PreserveAndMergeOwnedOutputStash(
            source,
            result,
            resolvedValues);

        merged.Should().BeSameAs(result);
        merged["stash"]!["Batch"].Should().BeSameAs(ownedReplacement);
        merged["stash"]!["Unrelated"].Should().NotBeSameAs(sourceUnrelated);
        JsonNode.DeepEquals(merged["stash"]!["Unrelated"], sourceUnrelated).Should().BeTrue();
        source.ToJsonString().Should().Be(sourceBefore);
        source["stash"]!["Batch"].Should().BeSameAs(sourceLargeArray);
        resolvedValues.Count.Should().Be(0);
        ownedReplacement.Parent.Should().BeSameAs(merged["stash"]);
    }

    [Fact(DisplayName = "Owned stash merge compares replacement keys with exact case")]
    public void S00002()
    {
        var source = new JsonObject
        {
            ["stash"] = new JsonObject
            {
                ["Batch"] = new JsonObject { ["Source"] = true }
            }
        };
        var ownedReplacement = new JsonObject { ["Resolved"] = true };
        var resolvedValues = new JsonObject
        {
            ["batch"] = ownedReplacement
        };

        var result = WorkflowStashHelper.PreserveAndMergeOwnedOutputStash(
            source,
            new JsonObject(),
            resolvedValues);

        result["stash"]!.AsObject().ContainsKey("Batch").Should().BeTrue();
        result["stash"]!.AsObject().ContainsKey("batch").Should().BeTrue();
        result["stash"]!["Batch"].Should().NotBeSameAs(source["stash"]!["Batch"]);
        result["stash"]!["batch"].Should().BeSameAs(ownedReplacement);
        resolvedValues.Count.Should().Be(0);
    }

    [Fact(DisplayName = "Owned stash merge keeps an explicit result stash instead of preserving source stash")]
    public void S00003()
    {
        var source = new JsonObject
        {
            ["stash"] = new JsonObject
            {
                ["SourceOnly"] = "source"
            }
        };
        var explicitStash = new JsonObject
        {
            ["Explicit"] = "result"
        };
        var result = new JsonObject
        {
            ["stash"] = explicitStash
        };
        var ownedValue = new JsonObject { ["Value"] = 1 };
        var resolvedValues = new JsonObject
        {
            ["Resolved"] = ownedValue
        };

        WorkflowStashHelper.PreserveAndMergeOwnedOutputStash(
            source,
            result,
            resolvedValues);

        result["stash"].Should().BeSameAs(explicitStash);
        explicitStash.ContainsKey("SourceOnly").Should().BeFalse();
        explicitStash["Explicit"]!.GetValue<string>().Should().Be("result");
        explicitStash["Resolved"].Should().BeSameAs(ownedValue);
        source["stash"]!["SourceOnly"]!.GetValue<string>().Should().Be("source");
        resolvedValues.Count.Should().Be(0);
    }

    [Fact(DisplayName = "Owned stash merge lets null resolved values replace source values and consumes the map")]
    public void S00004()
    {
        var sourceValue = new JsonObject { ["Source"] = true };
        var source = new JsonObject
        {
            ["stash"] = new JsonObject
            {
                ["Nullable"] = sourceValue,
                ["Preserved"] = "value"
            }
        };
        var sourceBefore = source.ToJsonString();
        var resolvedValues = new JsonObject
        {
            ["Nullable"] = null
        };

        var result = WorkflowStashHelper.PreserveAndMergeOwnedOutputStash(
            source,
            new JsonObject(),
            resolvedValues);

        result["stash"]!.AsObject().ContainsKey("Nullable").Should().BeTrue();
        result["stash"]!["Nullable"].Should().BeNull();
        result["stash"]!["Preserved"]!.GetValue<string>().Should().Be("value");
        result["stash"]!["Preserved"].Should().NotBeSameAs(source["stash"]!["Preserved"]);
        source.ToJsonString().Should().Be(sourceBefore);
        source["stash"]!["Nullable"].Should().BeSameAs(sourceValue);
        resolvedValues.Count.Should().Be(0);
    }
}
