using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Workflows;
using Reimaginate.Orchestrator.Common.Diagnostics;
using Reimaginate.Orchestrator.Common.Helpers;

namespace Reimaginate.Orchestrator.Common.Executors;

internal sealed class SwitchTaskExecutor(string id, string locationHint, IReadOnlyList<SwitchRoute> routes, JsonObject? environment = null) : Executor<JsonObject, JsonObject>(id)
{
    #region Constants

    private const string SelectedRoutePropertyName = "__selectedRoute";

    #endregion

    #region Execution

    public override ValueTask<JsonObject> HandleAsync(JsonObject currentObject, IWorkflowContext context, CancellationToken cancellationToken = default)
        => WorkflowTaskExecutionGuard.RunAsync(Id, locationHint, () => HandleCoreAsync(currentObject, context, cancellationToken));

    private async ValueTask<JsonObject> HandleCoreAsync(JsonObject currentObject, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        // Resolve the branch first so the same decision drives output shaping and downstream routing.
        var selectedRoute = ResolveRoute(currentObject);

        // If an output map is configured, project the payload for the selected branch; otherwise pass through.
        var output = selectedRoute.OutputMap is null
            ? (JsonObject)currentObject.DeepClone()
            : WorkflowEventInputTransformer.Transform(currentObject, selectedRoute.OutputMap, context, environment);
        var resolvedOutputStash = selectedRoute.OutputStashMap is null
            ? null
            : WorkflowEventInputTransformer.Transform(currentObject, selectedRoute.OutputStashMap, context, environment);

        output = WorkflowStashHelper.PreserveAndMergeOutputStash(currentObject, output, resolvedOutputStash);
        output[SelectedRoutePropertyName] = selectedRoute.TargetTaskName;
        await WorkflowExecutionTraceContext.RecordAsync("executor.switch.route_selected", new JsonObject
        {
            ["taskName"] = Id,
            ["targetTaskName"] = selectedRoute.TargetTaskName,
            ["when"] = selectedRoute.When,
            ["isDefault"] = selectedRoute.IsDefault,
            ["locationHint"] = selectedRoute.LocationHint
        }, cancellationToken);
        return output;
    }

    #endregion

    #region Route resolution

    public string ResolveTargetRoute(JsonObject currentObject)
    {
        return ResolveRoute(currentObject).TargetTaskName;
    }

    private SwitchRoute ResolveRoute(JsonObject currentObject)
    {
        SwitchRoute? defaultRoute = null;

        foreach (var route in routes)
        {
            if (route.IsDefault)
            {
                // Keep scanning so non-default matches always take precedence over the fallback route.
                defaultRoute = route;
                continue;
            }

            if (WorkflowConditionEvaluator.Evaluate(route.When, currentObject, environment))
            {
                return route;
            }
        }

        if (defaultRoute is not null)
        {
            return defaultRoute;
        }

        throw new InvalidOperationException($"Switch task '{Id}' did not match any branch and no default branch is configured.");
    }

    #endregion

    #region Public metadata

    public static string SelectedRouteProperty => SelectedRoutePropertyName;

    #endregion
}

internal sealed record SwitchRoute(string? When, string TargetTaskName, bool IsDefault, string LocationHint, JsonObject? OutputMap, JsonObject? OutputStashMap);
