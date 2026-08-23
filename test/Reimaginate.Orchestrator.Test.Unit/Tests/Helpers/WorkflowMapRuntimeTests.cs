using System.Text.Json.Nodes;
using FluentAssertions;
using Reimaginate.Orchestrator.Common.Helpers;
using Reimaginate.Orchestrator.Common.Services.WorkflowDefinitions.Compilation;
using Reimaginate.Orchestrator.DSL;
using Xunit;

namespace Reimaginate.Orchestrator.Test.Unit.Tests.Helpers;

public class WorkflowMapRuntimeTests
{
    [Fact(DisplayName = "Map runtime borrows source and environment nodes without mutating or aliasing them")]
    public void MapRuntimeBorrowsInputsWithoutMutationOrOutputAliasing()
    {
        var source = new JsonObject
        {
            ["source"] = new JsonObject
            {
                ["profile"] = new JsonObject { ["name"] = "Ada" },
                ["jobs"] = new JsonArray(
                    new JsonObject { ["employer"] = "Navy" },
                    new JsonObject { ["employer"] = "Industry" })
            }
        };
        var environment = new JsonObject { ["region"] = "AU" };
        var sourceBefore = source.ToJsonString();
        var environmentBefore = environment.ToJsonString();
        var definition = new MapDefinition
        {
            Input = "$.source",
            Rules =
            {
                new MapRuleDefinition { Target = "primary", From = "$.profile" },
                new MapRuleDefinition { Target = "secondary", From = "$.profile" },
                new MapRuleDefinition { Target = "region", From = "$.env.region" },
                new MapRuleDefinition
                {
                    Target = "jobs",
                    Foreach = "$.jobs",
                    Map = new MapDefinition
                    {
                        Rules =
                        {
                            new MapRuleDefinition { Target = "employer", From = "$.employer" },
                            new MapRuleDefinition { Target = "region", From = "$.env.region" }
                        }
                    }
                }
            }
        };

        var result = WorkflowMapRuntime.Execute(definition, source, environment);

        source.ToJsonString().Should().Be(sourceBefore);
        environment.ToJsonString().Should().Be(environmentBefore);
        result["region"]!.GetValue<string>().Should().Be("AU");
        result["jobs"]!.AsArray().Select(job => job!["region"]!.GetValue<string>())
            .Should().Equal("AU", "AU");

        result["primary"]!["name"] = "Changed";
        result["primary"]!["name"]!.GetValue<string>().Should().Be("Changed");
        result["secondary"]!["name"]!.GetValue<string>().Should().Be("Ada");
        source["source"]!["profile"]!["name"]!.GetValue<string>().Should().Be("Ada");
    }

    [Fact(DisplayName = "Simple projection detection excludes runtime overlays and expressions")]
    public void SimpleProjectionDetectionPreservesLegacyContextRequirements()
    {
        WorkflowMappingResolver.IsSimpleProjection(new JsonObject
        {
            ["Success"] = "$.Success",
            ["Items"] = "$.Results",
            ["Literal"] = "value"
        }).Should().BeTrue();

        WorkflowMappingResolver.IsSimpleProjection(new JsonObject
        {
            ["WorkflowId"] = "$.workflowInstanceId"
        }).Should().BeFalse();

        WorkflowMappingResolver.IsSimpleProjection(new JsonObject
        {
            ["Count"] = "count($.Results)"
        }).Should().BeFalse();
    }

    [Fact(DisplayName = "Map environment selectors prefer overlays and fall back to payload env values")]
    public void MapEnvironmentSelectorsPreferOverlayAndFallBackToPayload()
    {
        var source = new JsonObject
        {
            ["env"] = new JsonObject
            {
                ["value"] = "payload"
            }
        };
        var environment = new JsonObject
        {
            ["value"] = "overlay"
        };
        var definition = new MapDefinition
        {
            Rules =
            {
                new MapRuleDefinition { Target = "dot", From = "$.env.value" },
                new MapRuleDefinition { Target = "bracket", From = "$['env']['value']" }
            }
        };
        var environmentInputDefinition = new MapDefinition
        {
            Input = "$['env']",
            Rules =
            {
                new MapRuleDefinition { Target = "value", From = "$.value" }
            }
        };

        var overlaid = WorkflowMapRuntime.Execute(definition, source, environment);
        var payloadFallback = WorkflowMapRuntime.Execute(definition, source);
        var overlaidInput = WorkflowMapRuntime.Execute(environmentInputDefinition, source, environment);
        var payloadFallbackInput = WorkflowMapRuntime.Execute(environmentInputDefinition, source);

        overlaid["dot"]!.GetValue<string>().Should().Be("overlay");
        overlaid["bracket"]!.GetValue<string>().Should().Be("overlay");
        payloadFallback["dot"]!.GetValue<string>().Should().Be("payload");
        payloadFallback["bracket"]!.GetValue<string>().Should().Be("payload");
        overlaidInput["value"]!.GetValue<string>().Should().Be("overlay");
        payloadFallbackInput["value"]!.GetValue<string>().Should().Be("payload");
    }

    [Fact(DisplayName = "Compiled map plans preserve direct runtime behavior for nested maps and all rule sources")]
    public void CompiledPlanMatchesDirectRuntime()
    {
        var source = new JsonObject
        {
            ["payload"] = new JsonObject
            {
                ["enabled"] = true,
                ["name"] = " Ada ",
                ["env"] = new JsonObject { ["deployment region"] = "payload" },
                ["jobs"] = new JsonArray(
                    new JsonObject { ["employer"] = "Navy" },
                    new JsonObject { ["employer"] = "Industry" })
            }
        };
        var environment = new JsonObject
        {
            ["deployment region"] = "AU"
        };
        var definition = BuildRepresentativeDefinition();

        var direct = WorkflowMapRuntime.Execute(definition, source, environment);
        var compiled = WorkflowMapRuntime.Execute(WorkflowMapPlanCompiler.Compile(definition), source, environment);

        compiled.ToJsonString().Should().Be(direct.ToJsonString());
        compiled["name"]!.GetValue<string>().Should().Be("ADA");
        compiled["region"]!.GetValue<string>().Should().Be("AU");
        compiled["rootBracketRegion"]!.GetValue<string>().Should().Be("AU");
        compiled["missing"]!.GetValue<string>().Should().Be("fallback");
        compiled["jobCount"]!.GetValue<int>().Should().Be(2);
        compiled["jobs"]!.AsArray().Select(item => item!["employer"]!.GetValue<string>())
            .Should().Equal("NAVY", "INDUSTRY");

        compiled["constant"]!["kind"] = "changed";
        var second = WorkflowMapRuntime.Execute(WorkflowMapPlanCompiler.Compile(definition), source, environment);
        second["constant"]!["kind"]!.GetValue<string>().Should().Be("fixed");
        source["payload"]!["name"]!.GetValue<string>().Should().Be(" Ada ");
        environment["deployment region"]!.GetValue<string>().Should().Be("AU");
    }

    [Fact(DisplayName = "Compiled map plans snapshot mutable definitions while direct execution recompiles them")]
    public void CompiledPlanSnapshotsDefinitionAndDirectExecutionRecompiles()
    {
        var source = new JsonObject
        {
            ["payload"] = new JsonObject
            {
                ["enabled"] = true,
                ["name"] = " Ada ",
                ["replacement"] = "Grace",
                ["env"] = new JsonObject { ["deployment region"] = "payload" },
                ["jobs"] = new JsonArray(new JsonObject { ["employer"] = "Navy" })
            }
        };
        var definition = BuildRepresentativeDefinition();
        var compiled = WorkflowMapPlanCompiler.Compile(definition);

        definition.Rules[0].Target = "renamed";
        definition.Rules[0].From = "$.replacement";
        definition.Rules[0].Transform.Clear();
        definition.Rules.Single(rule => rule.Target == "jobCount").Expression = "count($.missingJobs)";
        ((JsonObject)definition.Rules.Single(rule => rule.Target == "constant").Constant!)["kind"] = "mutated";
        definition.Rules.Single(rule => rule.Target == "jobs").Map!.Rules[0].Target = "organisation";

        var compiledResult = WorkflowMapRuntime.Execute(compiled, source);
        var directResult = WorkflowMapRuntime.Execute(definition, source);

        compiledResult["name"]!.GetValue<string>().Should().Be("ADA");
        compiledResult["constant"]!["kind"]!.GetValue<string>().Should().Be("fixed");
        compiledResult["jobCount"]!.GetValue<int>().Should().Be(1);
        compiledResult["jobs"]![0]!["employer"]!.GetValue<string>().Should().Be("NAVY");
        compiledResult.ContainsKey("renamed").Should().BeFalse();

        directResult["renamed"]!.GetValue<string>().Should().Be("Grace");
        directResult["constant"]!["kind"]!.GetValue<string>().Should().Be("mutated");
        directResult["jobCount"]!.GetValue<int>().Should().Be(0);
    }

    [Fact(DisplayName = "Compiled map selectors reuse parsed JSON paths")]
    public void CompiledMapSelectorsReuseParsedJsonPaths()
    {
        var first = WorkflowMapPlanCompiler.Compile(BuildRepresentativeDefinition());
        var second = WorkflowMapPlanCompiler.Compile(BuildRepresentativeDefinition());

        first.Input.Path.Should().BeSameAs(second.Input.Path);
        first.Rules[0].From!.Path.Should().BeSameAs(second.Rules[0].From!.Path);
        first.Rules[1].From!.Path.Should().BeSameAs(second.Rules[1].From!.Path);
        first.Rules[1].From!.OverlayPath.Should().BeSameAs(second.Rules[1].From!.OverlayPath);
        first.Rules.Single(rule => rule.Target == "jobCount").Expression
            .Should().BeSameAs(second.Rules.Single(rule => rule.Target == "jobCount").Expression);
        first.Rules.Single(rule => rule.Target == "jobs").Foreach!.Path
            .Should().BeSameAs(second.Rules.Single(rule => rule.Target == "jobs").Foreach!.Path);
    }

    [Fact(DisplayName = "Compiled literal prototypes clone once into detached outputs and direct calls resnapshot mutations")]
    public void CompiledLiteralPrototypesProduceDetachedOutputsAndDirectCallsResnapshot()
    {
        var constant = new JsonObject { ["value"] = "compiled" };
        var fallback = new JsonObject { ["value"] = "compiled-default" };
        var definition = new MapDefinition
        {
            Rules =
            {
                new MapRuleDefinition
                {
                    Target = "constant",
                    Constant = constant,
                    HasConstant = true
                },
                new MapRuleDefinition
                {
                    Target = "fallback",
                    From = "$.missing",
                    Default = fallback,
                    HasDefault = true
                },
                new MapRuleDefinition
                {
                    Target = "explicitNull",
                    Constant = null,
                    HasConstant = true
                }
            }
        };
        var plan = WorkflowMapPlanCompiler.Compile(definition);

        constant["value"] = "mutated";
        fallback["value"] = "mutated-default";

        var first = WorkflowMapRuntime.Execute(plan, new JsonObject());
        var second = WorkflowMapRuntime.Execute(plan, new JsonObject());

        first["constant"]!.Should().NotBeSameAs(second["constant"]!);
        first["fallback"]!.Should().NotBeSameAs(second["fallback"]!);
        first["constant"]!["value"] = "changed-output";
        first["fallback"]!["value"] = "changed-output-default";
        second["constant"]!["value"]!.GetValue<string>().Should().Be("compiled");
        second["fallback"]!["value"]!.GetValue<string>().Should().Be("compiled-default");
        second.ContainsKey("explicitNull").Should().BeTrue();
        second["explicitNull"].Should().BeNull();

        var direct = WorkflowMapRuntime.Execute(definition, new JsonObject());
        direct["constant"]!["value"]!.GetValue<string>().Should().Be("mutated");
        direct["fallback"]!["value"]!.GetValue<string>().Should().Be("mutated-default");
    }

    [Fact(DisplayName = "Workflow compilation snapshots one reusable map plan with detached concurrent outputs")]
    public async Task WorkflowCompilationSnapshotsReusableMapPlanWithDetachedConcurrentOutputs()
    {
        var source = new JsonObject
        {
            ["payload"] = new JsonObject
            {
                ["enabled"] = true,
                ["name"] = " Ada ",
                ["env"] = new JsonObject { ["deployment region"] = "payload" },
                ["jobs"] = new JsonArray(new JsonObject { ["employer"] = "Navy" })
            }
        };
        var sourceBefore = source.ToJsonString();
        var definition = BuildRepresentativeDefinition();
        var mapTask = new MapTaskDefinition { Map = definition };
        var compilation = new WorkflowDefinitionCompiler().Compile(new BoundWorkflow(
            null,
            new Dictionary<string, TaskDefinition>(StringComparer.Ordinal)
            {
                ["Transform"] = mapTask
            },
            ["Transform"],
            []));

        compilation.IsSuccessful.Should().BeTrue();
        var mapPlan = compilation.Plan!.Nodes["Transform"].MapPlan;
        mapPlan.Should().NotBeNull();

        definition.Rules[0].Target = "mutatedName";
        definition.Rules[0].From = "$.notThere";
        ((JsonObject)definition.Rules.Single(rule => rule.Target == "constant").Constant!)["kind"] = "mutated";
        definition.Rules.Single(rule => rule.Target == "jobs").Map!.Rules[0].Target = "organisation";

        var environment = new JsonObject { ["deployment region"] = "overlay" };
        var outputs = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => WorkflowMapRuntime.Execute(mapPlan!, source, environment))));

        outputs.Should().OnlyContain(output =>
            output["name"]!.GetValue<string>() == "ADA"
            && output["constant"]!["kind"]!.GetValue<string>() == "fixed"
            && output["jobs"]![0]!["employer"]!.GetValue<string>() == "NAVY"
            && output["region"]!.GetValue<string>() == "overlay"
            && output["rootBracketRegion"]!.GetValue<string>() == "overlay");

        outputs[0]["constant"]!["kind"] = "changed";
        outputs[0]["jobs"]![0]!["employer"] = "Changed";
        outputs[1]["constant"]!["kind"]!.GetValue<string>().Should().Be("fixed");
        outputs[1]["jobs"]![0]!["employer"]!.GetValue<string>().Should().Be("NAVY");
        source.ToJsonString().Should().Be(sourceBefore);

        var anotherOutput = WorkflowMapRuntime.Execute(mapPlan!, source);
        anotherOutput["constant"]!["kind"]!.GetValue<string>().Should().Be("fixed");
        anotherOutput["region"]!.GetValue<string>().Should().Be("payload");
        anotherOutput["rootBracketRegion"]!.GetValue<string>().Should().Be("payload");
        anotherOutput.ContainsKey("mutatedName").Should().BeFalse();
    }

    private static MapDefinition BuildRepresentativeDefinition()
    {
        return new MapDefinition
        {
            Input = "$.payload",
            Rules =
            {
                new MapRuleDefinition
                {
                    Target = "name",
                    From = "$.name",
                    When = "$.enabled == true",
                    Transform = { "trim", "upper" }
                },
                new MapRuleDefinition
                {
                    Target = "region",
                    From = "$.env['deployment region']"
                },
                new MapRuleDefinition
                {
                    Target = "rootBracketRegion",
                    From = "$['env']['deployment region']"
                },
                new MapRuleDefinition
                {
                    Target = "missing",
                    From = "$.notThere",
                    Default = "fallback",
                    HasDefault = true
                },
                new MapRuleDefinition
                {
                    Target = "jobCount",
                    Expression = "count($.jobs)"
                },
                new MapRuleDefinition
                {
                    Target = "constant",
                    Constant = new JsonObject { ["kind"] = "fixed" },
                    HasConstant = true
                },
                new MapRuleDefinition
                {
                    Target = "profile",
                    Map = new MapDefinition
                    {
                        Rules =
                        {
                            new MapRuleDefinition { Target = "displayName", From = "$.name", Transform = { "trim" } }
                        }
                    }
                },
                new MapRuleDefinition
                {
                    Target = "jobs",
                    Foreach = "$.jobs",
                    Map = new MapDefinition
                    {
                        Rules =
                        {
                            new MapRuleDefinition { Target = "employer", From = "$.employer", Transform = { "upper" } }
                        }
                    }
                }
            }
        };
    }
}
