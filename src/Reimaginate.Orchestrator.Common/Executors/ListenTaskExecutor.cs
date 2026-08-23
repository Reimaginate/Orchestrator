using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Agents.AI.Workflows;
using Reimaginate.Orchestrator.Common.Diagnostics;
using Reimaginate.Orchestrator.Common.Helpers;

namespace Reimaginate.Orchestrator.Common.Executors;

internal sealed class ListenTaskExecutor(Channel<JsonObject> channel, string id, string locationHint, string? eventType = null, string? filterSignature = null, string? read = null, JsonObject? outputMap = null, JsonObject? outputStashMap = null, JsonObject? environment = null) : Executor<JsonObject, JsonObject>(id)
{
    public const string ContinueRoute = "__listen_continue";
    public const string HaltRoute = "__listen_halt";

    #region Listener target configuration

    private readonly IReadOnlyList<ListenTarget> _configuredTargets = BuildConfiguredTargets(eventType, filterSignature);
    private readonly string _readPropertyName = string.IsNullOrWhiteSpace(read) ? "event" : read;

    internal ListenTaskExecutor(Channel<JsonObject> channel, string id, string locationHint, IEnumerable<ListenTarget>? targets, string? read = null, JsonObject? outputMap = null, JsonObject? outputStashMap = null, JsonObject? environment = null)
        : this(channel, id, locationHint, null, null, read, outputMap, outputStashMap, environment)
    {
        // Normalize explicit targets from workflow definitions and fall back to defaults when none are supplied.
        _configuredTargets = BuildConfiguredTargets(targets);
    }

    #endregion

    #region Route registration

    public override ValueTask<JsonObject> HandleAsync(JsonObject currentObject, IWorkflowContext context, CancellationToken cancellationToken = default)
        => WorkflowTaskExecutionGuard.RunAsync(Id, locationHint, () => HandleCoreAsync(currentObject, context, cancellationToken));

    #endregion

    #region Message handling

    private async ValueTask<JsonObject> HandleCoreAsync(JsonObject currentObject, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        // Prefer explicit event payloads. If unavailable, opportunistically read the queued event.
        var incomingMessage = currentObject;
        if (incomingMessage["resumeEvent"] is not JsonObject && incomingMessage["event"] is not JsonObject && channel.Reader.TryRead(out var queuedMessage))
        {
            incomingMessage = queuedMessage;
        }

        // Resume events wrap payloads under resumeEvent while normal dispatches use event.
        var resumeEvent = incomingMessage["resumeEvent"]?["Payload"] as JsonObject
            ?? incomingMessage[_readPropertyName]?["Payload"] as JsonObject;

        if (resumeEvent is not null)
        {
            var previous = (JsonObject)currentObject.DeepClone();
            var listenContext = WorkflowEventPayloadContextHelper.BuildListenContext(resumeEvent, previous);

            if (MatchesAnyConfiguredTarget(resumeEvent, listenContext))
            {
                var ret = outputMap is null
                    ? listenContext
                    : WorkflowEventInputTransformer.Transform(listenContext, outputMap, context, environment);
                var resolvedOutputStash = outputStashMap is null
                    ? null
                    : WorkflowEventInputTransformer.Transform(listenContext, outputStashMap, context, environment);

                await WorkflowExecutionTraceContext.RecordAsync("executor.listen.matched", new JsonObject
                {
                    ["taskName"] = Id,
                    ["eventType"] = resumeEvent["eventType"]?.GetValue<string>() ?? resumeEvent["type"]?.GetValue<string>(),
                    ["configuredTargets"] = new JsonArray(_configuredTargets.Select(target => new JsonObject
                    {
                        ["eventType"] = target.EventType,
                        ["filterSignature"] = target.FilterSignature
                    }).ToArray())
                }, cancellationToken);
                var output = WorkflowStashHelper.PreserveAndMergeOutputStash((JsonObject)currentObject.DeepClone(), ret, resolvedOutputStash);
                output[SwitchTaskExecutor.SelectedRouteProperty] = ContinueRoute;
                return output;
            }
        }

        await WorkflowExecutionTraceContext.RecordAsync("executor.listen.halted", new JsonObject
        {
            ["taskName"] = Id,
            ["configuredTargets"] = new JsonArray(_configuredTargets.Select(target => new JsonObject
            {
                ["eventType"] = target.EventType,
                ["filterSignature"] = target.FilterSignature
            }).ToArray())
        }, cancellationToken);
        WorkflowExecutionTraceContext.AddActiveHaltingTaskName(Id);
        await context.RequestHaltAsync();
        var haltedOutput = (JsonObject)currentObject.DeepClone();
        haltedOutput[SwitchTaskExecutor.SelectedRouteProperty] = HaltRoute;
        return haltedOutput;
    }

    #endregion

    #region Target matching

    private bool MatchesAnyConfiguredTarget(JsonObject resumeEvent, JsonObject listenContext)
    {
        var actualEventType = resumeEvent["eventType"]?.GetValue<string>()
                              ?? resumeEvent["type"]?.GetValue<string>();

        return _configuredTargets.Any(target =>
            MatchesEventType(target.EventType, actualEventType)
            && WorkflowConditionEvaluator.Evaluate(target.FilterSignature, listenContext, environment));
    }

    private static bool MatchesEventType(string expectedEventType, string? actualEventType)
    {
        if (string.Equals(expectedEventType, "*", StringComparison.Ordinal))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(actualEventType)
               && string.Equals(expectedEventType, actualEventType, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Target normalization

    private static IReadOnlyList<ListenTarget> BuildConfiguredTargets(IEnumerable<ListenTarget>? targets)
    {
        // Keep configured targets deterministic and ensure missing values are converted to wildcard/always-true semantics.
        var normalizedTargets = targets?
            .Select(target => new ListenTarget(
                string.IsNullOrWhiteSpace(target.EventType) ? "*" : target.EventType,
                string.IsNullOrWhiteSpace(target.FilterSignature) ? "true" : target.FilterSignature))
            .Distinct()
            .ToList();

        return normalizedTargets is not { Count: > 0 }
            ? [new ListenTarget("*", "true")]
            : normalizedTargets;
    }

    private static IReadOnlyList<ListenTarget> BuildConfiguredTargets(string? eventType, string? filterSignature)
    {
        return BuildConfiguredTargets([new ListenTarget(
            string.IsNullOrWhiteSpace(eventType) ? "*" : eventType,
            string.IsNullOrWhiteSpace(filterSignature) ? "true" : filterSignature)]);
    }

    #endregion

    #region Models

    internal sealed record ListenTarget(string EventType, string FilterSignature);

    #endregion
}
