using System.Text.Json.Nodes;
using Reimaginate.Orchestrator.DSL;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowDefinitions.Compilation;

internal static class WorkflowDiagnosticsGraphBuilder
{
    public static JsonObject Build(string workflowType, WorkflowDefinition definition, WorkflowCompilationPlan plan)
    {
        return new JsonObject
        {
            ["workflowType"] = workflowType,
            ["generatedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["startTaskName"] = plan.StartTaskName,
            ["rootTaskNames"] = BuildStringArray(plan.RootTaskNames),
            ["nodes"] = new JsonArray(plan.Nodes.Values.OrderBy(node => node.Name, StringComparer.Ordinal).Select(BuildNode).ToArray()),
            ["transitions"] = new JsonArray(plan.Transitions.Select(transition => new JsonObject
            {
                ["fromTaskName"] = transition.FromTaskName,
                ["toTaskName"] = transition.ToTaskName,
                ["locationHint"] = transition.LocationHint,
                ["routeTaskName"] = transition.RouteTaskName
            }).ToArray()),
            ["branches"] = new JsonArray(plan.BranchesByTask.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(BuildBranchGroup).ToArray()),
            ["constraints"] = new JsonArray(plan.Constraints.Select(constraint => new JsonObject
            {
                ["kind"] = constraint.Kind.ToString(),
                ["taskName"] = constraint.TaskName,
                ["locationHint"] = constraint.LocationHint,
                ["description"] = constraint.Description
            }).ToArray()),
            ["awaitingEvents"] = new JsonArray(definition.Do
                .SelectMany(entry => EnumerateHaltingTasks(entry.Key, entry.Value))
                .Select(entry => new JsonObject
                {
                    ["taskName"] = entry.TaskName,
                    ["kind"] = entry.Definition.GetType().Name
                })
                .ToArray())
        };
    }

    private static JsonObject BuildNode(WorkflowPlanNode node)
    {
        return new JsonObject
        {
            ["name"] = node.Name,
            ["kind"] = node.Kind.ToString(),
            ["locationHint"] = node.LocationHint,
            ["nextTaskNames"] = BuildStringArray(node.NextTaskNames),
            ["condition"] = node.Condition,
            ["callTarget"] = node.CallTarget,
            ["doLoopMode"] = node.DoLoopMode?.ToString(),
            ["loopSourceExpression"] = node.LoopSourceExpression,
            ["loopItemVariable"] = node.LoopItemVariable,
            ["loopIndexVariable"] = node.LoopIndexVariable,
            ["loopBatchSize"] = node.LoopBatchSize,
            ["loopMaxConcurrency"] = node.LoopMaxConcurrency,
            ["loopInputMap"] = node.LoopInputMap?.DeepClone(),
            ["loopCollectVariable"] = node.LoopCollectVariable,
            ["loopCollectInclude"] = node.LoopCollectInclude is null
                ? null
                : new JsonArray(node.LoopCollectInclude.Select(field => JsonValue.Create(field)).ToArray()),
            ["loopErrorMode"] = node.LoopErrorMode,
            ["captureErrors"] = node.CaptureErrors,
            ["sourceWorkflowType"] = node.SourceWorkflowType,
            ["parentRunTaskName"] = node.ParentRunTaskName,
            ["inputMap"] = node.InputMap?.DeepClone(),
            ["outputMap"] = node.OutputMap?.DeepClone(),
            ["outputStashMap"] = node.OutputStashMap?.DeepClone()
        };
    }

    private static JsonObject BuildBranchGroup(KeyValuePair<string, IReadOnlyList<WorkflowPlanBranch>> entry)
    {
        return new JsonObject
        {
            ["taskName"] = entry.Key,
            ["branches"] = new JsonArray(entry.Value.Select(branch => new JsonObject
            {
                ["when"] = branch.When,
                ["targetTaskName"] = branch.TargetTaskName,
                ["routeTaskName"] = branch.RouteTaskName,
                ["isDefault"] = branch.IsDefault,
                ["locationHint"] = branch.LocationHint,
                ["outputMap"] = branch.OutputMap?.DeepClone(),
                ["outputStashMap"] = branch.OutputStashMap?.DeepClone()
            }).ToArray())
        };
    }

    private static JsonArray BuildStringArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static IEnumerable<(string TaskName, TaskDefinition Definition)> EnumerateHaltingTasks(string taskName, TaskDefinition definition)
    {
        if (definition is ListenTaskDefinition or WaitTaskDefinition)
        {
            yield return (taskName, definition);
        }

        switch (definition)
        {
            case DoTaskDefinition doTask:
                foreach (var nested in doTask.Do.SelectMany(entry => EnumerateHaltingTasks(entry.Key, entry.Value)))
                {
                    yield return nested;
                }

                break;
            case ForkTaskDefinition forkTask:
                foreach (var nested in forkTask.Branches.SelectMany(branch => branch.Do.SelectMany(entry => EnumerateHaltingTasks(entry.Key, entry.Value))))
                {
                    yield return nested;
                }

                break;
            case TryTaskDefinition tryTask:
                foreach (var nested in tryTask.Try.SelectMany(entry => EnumerateHaltingTasks(entry.Key, entry.Value)))
                {
                    yield return nested;
                }

                foreach (var nested in tryTask.Catch.SelectMany(c => c.Do.SelectMany(entry => EnumerateHaltingTasks(entry.Key, entry.Value))))
                {
                    yield return nested;
                }

                if (tryTask.Finally is not null)
                {
                    foreach (var nested in tryTask.Finally.SelectMany(entry => EnumerateHaltingTasks(entry.Key, entry.Value)))
                    {
                        yield return nested;
                    }
                }

                break;
            case RunTaskDefinition runTask when runTask.Inline is not null:
                foreach (var nested in runTask.Inline.Do.SelectMany(entry => EnumerateHaltingTasks(entry.Key, entry.Value)))
                {
                    yield return nested;
                }

                break;
        }
    }
}
