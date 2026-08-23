using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml;
using Microsoft.Agents.AI.Workflows;
using Reimaginate.Orchestrator.Common.Diagnostics;
using Reimaginate.Orchestrator.Common.Helpers;

namespace Reimaginate.Orchestrator.Common.Executors;

internal sealed class WaitTaskExecutor(string id, string locationHint, string? waitFor, string? waitUntil, JsonObject? outputMap = null, JsonObject? outputStashMap = null, string? condition = null, JsonObject? environment = null) : Executor<JsonObject, JsonObject>(id)
{
    private const string WaitStateProperty = "__wait";

    public override ValueTask<JsonObject> HandleAsync(JsonObject currentObject, IWorkflowContext context, CancellationToken cancellationToken = default)
        => WorkflowTaskExecutionGuard.RunAsync(Id, locationHint, () => HandleCoreAsync(currentObject, context, cancellationToken));

    private async ValueTask<JsonObject> HandleCoreAsync(JsonObject currentObject, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        if (!WorkflowConditionEvaluator.Evaluate(condition, currentObject, environment))
        {
            return (JsonObject)currentObject.DeepClone();
        }

        var now = DateTimeOffset.UtcNow;
        var nextWakeAt = ResolveWakeTime(currentObject, now);

        if (nextWakeAt > now)
        {
            var suspendedPayload = (JsonObject)currentObject.DeepClone();
            suspendedPayload[WaitStateProperty] = new JsonObject
            {
                ["task"] = Id,
                ["untilUtc"] = nextWakeAt.ToString("O", CultureInfo.InvariantCulture)
            };

            await WorkflowExecutionTraceContext.RecordAsync("executor.wait.suspended", new JsonObject
            {
                ["taskName"] = Id,
                ["untilUtc"] = nextWakeAt.ToString("O", CultureInfo.InvariantCulture)
            }, cancellationToken);
            WorkflowExecutionTraceContext.AddActiveHaltingTaskName(Id);
            await context.RequestHaltAsync();
            return suspendedPayload;
        }

        var completePayload = (JsonObject)currentObject.DeepClone();
        if (outputMap is null && outputStashMap is null)
        {
            return completePayload;
        }

        var merged = new JsonObject
        {
            ["previous"] = completePayload,
            ["wait"] = new JsonObject
            {
                ["task"] = Id,
                ["untilUtc"] = nextWakeAt.ToString("O", CultureInfo.InvariantCulture),
                ["resumedAtUtc"] = now.ToString("O", CultureInfo.InvariantCulture)
            }
        };

        await WorkflowExecutionTraceContext.RecordAsync("executor.wait.completed", new JsonObject
        {
            ["taskName"] = Id,
            ["untilUtc"] = nextWakeAt.ToString("O", CultureInfo.InvariantCulture),
            ["resumedAtUtc"] = now.ToString("O", CultureInfo.InvariantCulture)
        }, cancellationToken);

        var output = outputMap is null
            ? completePayload
            : WorkflowEventInputTransformer.Transform(merged, outputMap, context, environment);
        var resolvedOutputStash = outputStashMap is null
            ? null
            : WorkflowEventInputTransformer.Transform(merged, outputStashMap, context, environment);

        return WorkflowStashHelper.PreserveAndMergeOutputStash(currentObject, output, resolvedOutputStash);
    }

    private DateTimeOffset ResolveWakeTime(JsonObject payload, DateTimeOffset now)
    {
        var persistedUntil = payload[WaitStateProperty]?["untilUtc"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(persistedUntil)
            && DateTimeOffset.TryParse(persistedUntil, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var persistedWakeAt))
        {
            return persistedWakeAt.ToUniversalTime();
        }

        if (!string.IsNullOrWhiteSpace(waitFor) && XmlConvert.ToTimeSpan(waitFor) is { } duration)
        {
            return now.Add(duration);
        }

        if (!string.IsNullOrWhiteSpace(waitUntil)
            && DateTimeOffset.TryParse(waitUntil, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var absoluteWakeAt))
        {
            return absoluteWakeAt.ToUniversalTime();
        }

        return now;
    }
}
