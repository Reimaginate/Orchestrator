using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using NSubstitute;
using Reimaginate.Orchestrator.Common.Helpers;
using Xunit;

namespace Reimaginate.Orchestrator.Test.Unit.Tests.Helpers;

public class WorkflowMappingResolverTests
{
    [Fact(DisplayName = "Mapping resolution borrows primary fallback environment and runtime metadata for dot and bracket paths")]
    public void S00001()
    {
        var primary = new JsonObject
        {
            ["Primary"] = new JsonObject { ["Value"] = "primary" },
            ["env"] = new JsonObject { ["Region"] = "payload" },
            ["workflowInstanceId"] = "payload-id",
            ["workflow"] = new JsonObject { ["ShouldBeHidden"] = true }
        };
        var fallback = new JsonObject
        {
            ["Fallback"] = new JsonObject { ["Value"] = "fallback" }
        };
        var environment = new JsonObject { ["Region"] = "overlay" };
        var primaryBefore = primary.ToJsonString();
        var fallbackBefore = fallback.ToJsonString();
        var environmentBefore = environment.ToJsonString();
        var workflowContext = Substitute.For<IWorkflowContext>();
        workflowContext.TraceContext.Returns(new Dictionary<string, string>
        {
            ["runId"] = "wf-123",
            ["workflowType"] = "mapping.workflow.yaml"
        });
        var template = new JsonObject
        {
            ["PrimaryDot"] = "$.Primary.Value",
            ["FallbackBracket"] = "$['Fallback']['Value']",
            ["EnvironmentBracket"] = "$['env']['Region']",
            ["RootInstance"] = "$.workflowInstanceId",
            ["InstanceBracket"] = "$['workflow']['workflowInstanceId']",
            ["TypeDot"] = "$.workflow.workflowType",
            ["MixedContext"] = "{{ $.env.Region }}|{{ $.workflow.workflowType }}",
            ["HiddenPayloadWorkflowValue"] = "$.workflow.ShouldBeHidden"
        };

        var result = WorkflowMappingResolver.ResolveJsonNode(
            template,
            primary,
            fallback,
            workflowContext: workflowContext,
            environment: environment)!.AsObject();

        result["PrimaryDot"]!.GetValue<string>().Should().Be("primary");
        result["FallbackBracket"]!.GetValue<string>().Should().Be("fallback");
        result["EnvironmentBracket"]!.GetValue<string>().Should().Be("overlay");
        result["RootInstance"]!.GetValue<string>().Should().Be("wf-123");
        result["InstanceBracket"]!.GetValue<string>().Should().Be("wf-123");
        result["TypeDot"]!.GetValue<string>().Should().Be("mapping.workflow.yaml");
        result["MixedContext"]!.GetValue<string>().Should().Be("overlay|mapping.workflow.yaml");
        result["HiddenPayloadWorkflowValue"].Should().BeNull();
        primary.ToJsonString().Should().Be(primaryBefore);
        fallback.ToJsonString().Should().Be(fallbackBefore);
        environment.ToJsonString().Should().Be(environmentBefore);

        result["PrimaryDot"] = "changed";
        primary["Primary"]!["Value"]!.GetValue<string>().Should().Be("primary");
    }

    [Fact(DisplayName = "Mapping resolution preserves exact root null fallback and primary-only interpolation semantics")]
    public void S00002()
    {
        var primary = new JsonObject
        {
            ["Value"] = null,
            ["Name"] = "Primary"
        };
        var fallback = new JsonObject
        {
            ["Value"] = "fallback",
            ["OnlyFallback"] = "not-interpolated"
        };
        var template = new JsonObject
        {
            ["Direct"] = "$.Value",
            ["Expression"] = "coalesce($.Value, \"local\")",
            ["Mixed"] = "Hello {{ $.Name }} {{ $.OnlyFallback }}",
            ["LegacyHandlebars"] = "{{Name}}",
            ["Raw"] = "$"
        };

        var result = WorkflowMappingResolver.ResolveJsonNode(
            template,
            primary,
            fallback)!.AsObject();

        result["Direct"]!.GetValue<string>().Should().Be("fallback");
        result["Expression"]!.GetValue<string>().Should().Be("local");
        result["Mixed"]!.GetValue<string>().Should().Be("Hello Primary ");
        result["LegacyHandlebars"]!.GetValue<string>().Should().Be("Primary");
        JsonNode.DeepEquals(result["Raw"], primary).Should().BeTrue();
        result["Raw"].Should().NotBeSameAs(primary);
        result["Raw"]!.AsObject().ContainsKey("workflow").Should().BeFalse();
        result["Raw"]!.AsObject().ContainsKey("env").Should().BeFalse();
    }

    [Fact(DisplayName = "Sequential stash mappings expose earlier values and replace existing subtrees without mutating input")]
    public void S00003()
    {
        var payload = new JsonObject
        {
            ["Input"] = "A",
            ["stash"] = new JsonObject
            {
                ["Existing"] = new JsonObject
                {
                    ["Old"] = true,
                    ["Keep"] = "old"
                }
            }
        };
        var payloadBefore = payload.ToJsonString();
        var template = new JsonObject
        {
            ["First"] = "$.Input",
            ["Second"] = "$.stash.First + \"-B\"",
            ["Existing"] = "{ New: $.stash.Second }",
            ["RemovedOldLeaf"] = "$['stash']['Existing']['Keep']",
            ["ExplicitNull"] = null,
            ["ObservedNull"] = "isNull($.stash.ExplicitNull)"
        };

        var result = WorkflowMappingResolver.ResolveSequentialStashValues(template, payload);

        result["First"]!.GetValue<string>().Should().Be("A");
        result["Second"]!.GetValue<string>().Should().Be("A-B");
        result["Existing"]!["New"]!.GetValue<string>().Should().Be("A-B");
        result["RemovedOldLeaf"].Should().BeNull();
        result["ExplicitNull"].Should().BeNull();
        result["ObservedNull"]!.GetValue<bool>().Should().BeTrue();
        payload.ToJsonString().Should().Be(payloadBefore);

        result["Existing"]!["New"] = "changed";
        payload["stash"]!["Existing"]!["Keep"]!.GetValue<string>().Should().Be("old");
    }

    [Fact(DisplayName = "Event mapping uses event resume and initiating precedence without reparenting source nodes")]
    public void S00004()
    {
        var primaryEvent = new JsonObject
        {
            ["Payload"] = new JsonObject { ["Source"] = "event", ["Id"] = "1" }
        };
        var resumeEvent = new JsonObject
        {
            ["Payload"] = new JsonObject { ["Source"] = "resume", ["Id"] = "2" }
        };
        var initiatingEvent = new JsonObject
        {
            ["Payload"] = new JsonObject { ["Source"] = "initiating", ["Id"] = "3" }
        };
        var payload = new JsonObject
        {
            ["event"] = primaryEvent,
            ["resumeEvent"] = resumeEvent,
            ["__initiatingEvent"] = initiatingEvent
        };
        var before = payload.ToJsonString();
        var template = new JsonObject
        {
            ["RootEvent"] = "$.event.Source",
            ["WorkflowEvent"] = "$['workflow']['event']['Id']",
            ["WorkflowResumeEvent"] = "$.workflow.resumeEvent.Source",
            ["RawResumeEvent"] = "$.resumeEvent.Payload.Source"
        };

        var result = WorkflowMappingResolver.ResolveJsonNode(template, payload)!.AsObject();

        result["RootEvent"]!.GetValue<string>().Should().Be("event");
        result["WorkflowEvent"]!.GetValue<string>().Should().Be("1");
        result["WorkflowResumeEvent"]!.GetValue<string>().Should().Be("event");
        result["RawResumeEvent"]!.GetValue<string>().Should().Be("resume");
        payload.ToJsonString().Should().Be(before);
        primaryEvent.Parent.Should().BeSameAs(payload);
        resumeEvent.Parent.Should().BeSameAs(payload);
        initiatingEvent.Parent.Should().BeSameAs(payload);
    }
}
