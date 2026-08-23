using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Workflows;
using Reimaginate.Orchestrator.Common.Diagnostics;

namespace Reimaginate.Orchestrator.Common.Helpers;

internal static class WorkflowMappingResolver
{
    private static readonly HashSet<string> ValueExpressionFunctions = new(StringComparer.Ordinal)
    {
        "startsWith",
        "endsWith",
        "contains",
        "matches",
        "toLower",
        "toUpper",
        "trim",
        "exists",
        "isNull",
        "isNotNull",
        "isEmpty",
        "isNotEmpty",
        "in",
        "count",
        "any",
        "all",
        "pluck",
        "select",
        "where",
        "compact",
        "distinct",
        "setEquals",
        "isSubset",
        "first",
        "last",
        "maxBy",
        "minBy",
        "orderBy",
        "coalesce",
        "defaultIfEmpty",
        "join",
        "split",
        "merge",
        "set",
        "append",
        "insert",
        "removeWhere",
        "removeAt"
    };

    public static JsonObject BuildContext(JsonObject payload, IWorkflowContext? workflowContext = null, JsonObject? environment = null)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var context = WorkflowEnvironmentContext.Enrich(payload, environment);
        var workflow = new JsonObject();
        var metadata = WorkflowRuntimeMetadataResolver.Resolve(workflowContext);

        if (!string.IsNullOrWhiteSpace(metadata.WorkflowInstanceId))
        {
            workflow["workflowInstanceId"] = JsonValue.Create(metadata.WorkflowInstanceId);
            context["workflowInstanceId"] = JsonValue.Create(metadata.WorkflowInstanceId);
        }

        if (!string.IsNullOrWhiteSpace(metadata.WorkflowType))
        {
            workflow["workflowType"] = JsonValue.Create(metadata.WorkflowType);
        }

        var workflowEvent = ResolveWorkflowEvent(payload);
        if (workflowEvent is not null)
        {
            workflow["event"] = workflowEvent.DeepClone();
            workflow["resumeEvent"] = workflowEvent.DeepClone();
            context["event"] = workflowEvent.DeepClone();
        }

        context["workflow"] = workflow;
        return context;
    }

    public static JsonObject Transform(JsonObject payload, JsonObject? template, IWorkflowContext? workflowContext = null, JsonObject? environment = null)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (template is null)
        {
            return payload.DeepClone().AsObject();
        }

        var context = MappingResolutionContext.Create(
            payload,
            fallbackPayload: null,
            exactRootPayload: payload,
            workflowContext,
            environment);
        return ResolveJsonNodeCore(template, context) as JsonObject
            ?? throw new InvalidOperationException("Resolved mapping template was not a JSON object.");
    }

    public static JsonObject ResolveSequentialStashValues(
        JsonObject template,
        JsonObject payload,
        IWorkflowContext? workflowContext = null,
        JsonObject? environment = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(payload);

        var resolved = new JsonObject();
        var context = MappingResolutionContext.Create(
            payload,
            fallbackPayload: null,
            exactRootPayload: payload,
            workflowContext,
            environment,
            resolved);
        foreach (var (key, value) in template)
        {
            // Every resolver path returns a detached node. Attaching it once to the
            // result also makes it visible to later assignments through StashOverlay.
            resolved[key] = ResolveJsonNodeCore(value, context);
        }

        return resolved;
    }

    public static JsonNode? ResolveJsonNode(JsonNode? template, JsonObject primaryPayload, JsonObject? fallbackPayload = null, JsonObject? exactRootPayload = null, IWorkflowContext? workflowContext = null, JsonObject? environment = null)
    {
        ArgumentNullException.ThrowIfNull(primaryPayload);

        var context = MappingResolutionContext.Create(
            primaryPayload,
            fallbackPayload,
            exactRootPayload ?? primaryPayload,
            workflowContext,
            environment);
        return ResolveJsonNodeCore(template, context);
    }

    public static JsonObject ResolveProjection(
        JsonObject template,
        JsonObject primaryPayload,
        JsonObject? fallbackPayload = null,
        JsonObject? exactRootPayload = null,
        IWorkflowContext? workflowContext = null,
        JsonObject? environment = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(primaryPayload);

        return ResolveProjectionNode(template) as JsonObject
               ?? throw new InvalidOperationException("Resolved projection was not a JSON object.");

        JsonNode? ResolveProjectionNode(JsonNode? node)
        {
            if (node is JsonObject objectNode)
            {
                var projected = new JsonObject();
                foreach (var (key, value) in objectNode)
                {
                    projected[key] = ResolveProjectionNode(value);
                }

                return projected;
            }

            if (node is JsonArray arrayNode)
            {
                return new JsonArray(arrayNode.Select(ResolveProjectionNode).ToArray());
            }

            if (node is not JsonValue valueNode || !valueNode.TryGetValue<string>(out var raw))
            {
                return node?.DeepClone();
            }

            var trimmed = raw.Trim();
            if (trimmed == "$")
            {
                return (exactRootPayload ?? primaryPayload).DeepClone();
            }

            if (IsDirectPath(trimmed))
            {
                if (!IsSimpleProjectionString(raw))
                {
                    return ResolveJsonNode(
                        valueNode,
                        primaryPayload,
                        fallbackPayload,
                        exactRootPayload,
                        workflowContext,
                        environment);
                }

                var selected = JsonPath.SelectToken(primaryPayload, trimmed)
                               ?? (fallbackPayload is null ? null : JsonPath.SelectToken(fallbackPayload, trimmed));
                if (selected is not null)
                {
                    return selected.DeepClone();
                }
            }
            else if (!IsValueExpressionCandidate(raw)
                     && !raw.Contains("{{", StringComparison.Ordinal))
            {
                return valueNode.DeepClone();
            }

            return ResolveJsonNode(
                valueNode,
                primaryPayload,
                fallbackPayload,
                exactRootPayload,
                workflowContext,
                environment);
        }
    }

    public static JsonObject? ResolveBestEffortProjection(
        JsonObject template,
        JsonObject primaryPayload,
        JsonObject? fallbackPayload = null,
        JsonObject? exactRootPayload = null,
        IWorkflowContext? workflowContext = null,
        JsonObject? environment = null)
        => IsSimpleProjection(template)
            ? ResolveProjection(template, primaryPayload, fallbackPayload, exactRootPayload, workflowContext, environment)
            : ResolveJsonNode(template, primaryPayload, fallbackPayload, exactRootPayload, workflowContext, environment) as JsonObject;

    internal static bool IsSimpleProjection(JsonNode? template)
    {
        return template switch
        {
            null => true,
            JsonObject obj => obj.All(entry => IsSimpleProjection(entry.Value)),
            JsonArray array => array.All(IsSimpleProjection),
            JsonValue value when value.TryGetValue<string>(out var raw) => IsSimpleProjectionString(raw),
            _ => true
        };
    }

    public static JsonNode? ResolveTemplate(string? template, JsonObject primaryPayload, JsonObject? fallbackPayload = null, JsonObject? exactRootPayload = null, IWorkflowContext? workflowContext = null, JsonObject? environment = null)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return template is null ? null : JsonValue.Create(template);
        }

        var context = MappingResolutionContext.Create(
            primaryPayload,
            fallbackPayload,
            exactRootPayload ?? primaryPayload,
            workflowContext,
            environment);
        return ResolveScalar(template, context);
    }

    private static JsonNode? ResolveJsonNodeCore(
        JsonNode? template,
        MappingResolutionContext context)
    {
        return template switch
        {
            null => null,
            JsonObject obj => ResolveObject(obj, context),
            JsonArray array => ResolveArray(array, context),
            JsonValue valueNode when valueNode.TryGetValue<string>(out var raw) => ResolveScalar(raw, context),
            _ => template.DeepClone()
        };
    }

    private static JsonObject ResolveObject(
        JsonObject template,
        MappingResolutionContext context)
    {
        var resolved = new JsonObject();
        foreach (var (key, value) in template)
        {
            resolved[key] = ResolveJsonNodeCore(value, context);
        }

        return resolved;
    }

    private static JsonArray ResolveArray(
        JsonArray template,
        MappingResolutionContext context)
    {
        var resolved = new JsonArray();
        foreach (var item in template)
        {
            resolved.Add(ResolveJsonNodeCore(item, context));
        }

        return resolved;
    }

    private static JsonNode? ResolveScalar(
        string raw,
        MappingResolutionContext context)
    {
        if (raw.Trim() == "$")
        {
            return context.ExactRootPayload.DeepClone();
        }

        if (IsValueExpressionCandidate(raw))
        {
            return WorkflowValueExpressionEvaluator.Evaluate(
                raw,
                context.CombinedScope);
        }

        var trimmed = raw.Trim();
        if (IsDirectPath(trimmed))
        {
            return context.ResolveDirectPath(trimmed);
        }

        return WorkflowTemplateRenderer.ResolveTemplate(
            raw,
            context.PrimaryScope,
            context.BuildLegacyPrimaryContext);
    }

    private static bool IsValueExpressionCandidate(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith("{{", StringComparison.Ordinal))
        {
            return false;
        }

        if (trimmed[0] is '{' or '[')
        {
            return true;
        }

        if (StartsWithKnownValueFunctionCall(trimmed))
        {
            return true;
        }

        return trimmed.StartsWith("$", StringComparison.Ordinal)
            && ContainsTopLevelPlus(trimmed);
    }

    private static bool IsSimpleProjectionString(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Contains("{{", StringComparison.Ordinal) || IsValueExpressionCandidate(trimmed))
        {
            return false;
        }

        if (trimmed.StartsWith("$[", StringComparison.Ordinal))
        {
            // Bracket-root selectors can address virtual mapping roots such as
            // env/workflow/stash. Route them through the read-only scope.
            return false;
        }

        if (!trimmed.StartsWith("$.", StringComparison.Ordinal))
        {
            return true;
        }

        return !trimmed.Equals("$.workflow", StringComparison.Ordinal)
               && !trimmed.StartsWith("$.workflow.", StringComparison.Ordinal)
               && !trimmed.Equals("$.workflowInstanceId", StringComparison.Ordinal)
               && !trimmed.Equals("$.event", StringComparison.Ordinal)
               && !trimmed.StartsWith("$.event.", StringComparison.Ordinal)
               && !trimmed.Equals("$.resumeEvent", StringComparison.Ordinal)
               && !trimmed.StartsWith("$.resumeEvent.", StringComparison.Ordinal)
               && !trimmed.Equals("$.env", StringComparison.Ordinal)
               && !trimmed.StartsWith("$.env.", StringComparison.Ordinal);
    }

    private static bool IsDirectPath(string value)
        => value.StartsWith("$.", StringComparison.Ordinal)
           || value.StartsWith("$[", StringComparison.Ordinal);

    private static bool StartsWithKnownValueFunctionCall(string trimmed)
    {
        var parenIndex = trimmed.IndexOf('(', StringComparison.Ordinal);
        if (parenIndex <= 0)
        {
            return false;
        }

        var identifier = trimmed[..parenIndex].Trim();
        return IsIdentifier(identifier) && ValueExpressionFunctions.Contains(identifier);
    }

    private static bool IsIdentifier(string value)
    {
        if (value.Length == 0 || !IsIdentifierStart(value[0]))
        {
            return false;
        }

        return value.Skip(1).All(IsIdentifierPart);
    }

    private static bool IsIdentifierStart(char character)
        => char.IsLetter(character) || character == '_';

    private static bool IsIdentifierPart(char character)
        => char.IsLetterOrDigit(character) || character == '_';

    private static bool ContainsTopLevelPlus(string value)
    {
        var depth = 0;
        char? quote = null;
        var escaped = false;

        foreach (var character in value)
        {
            if (quote is not null)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (character == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (character == quote)
                {
                    quote = null;
                }

                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                continue;
            }

            if (character is '(' or '[' or '{')
            {
                depth++;
                continue;
            }

            if (character is ')' or ']' or '}')
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (character == '+' && depth == 0)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class MappingResolutionContext
    {
        private readonly JsonObject _primaryPayload;
        private readonly IWorkflowContext? _workflowContext;
        private readonly JsonObject? _environment;
        private readonly JsonObject? _stashOverlay;
        private readonly Lazy<WorkflowEvaluationScope?> _fallbackScope;

        private MappingResolutionContext(
            JsonObject primaryPayload,
            JsonObject? fallbackPayload,
            JsonObject exactRootPayload,
            IWorkflowContext? workflowContext,
            JsonObject? environment,
            JsonObject? stashOverlay)
        {
            _primaryPayload = primaryPayload;
            _workflowContext = workflowContext;
            _environment = environment;
            _stashOverlay = stashOverlay;
            ExactRootPayload = exactRootPayload;

            var metadata = WorkflowRuntimeMetadataResolver.Resolve(workflowContext);
            var primaryEvent = ResolveWorkflowEventView(primaryPayload);
            var fallbackEvent = fallbackPayload is null
                ? null
                : ResolveWorkflowEventView(fallbackPayload);

            var primaryRuntime = new WorkflowRuntimeEvaluationOverlay(
                metadata.WorkflowType,
                metadata.WorkflowInstanceId,
                primaryEvent,
                primaryEvent is not null);
            var combinedRuntime = new WorkflowRuntimeEvaluationOverlay(
                metadata.WorkflowType,
                metadata.WorkflowInstanceId,
                primaryEvent,
                primaryEvent is not null,
                fallbackEvent,
                fallbackEvent is not null);

            PrimaryScope = new WorkflowEvaluationScope(
                primaryPayload,
                environment,
                Runtime: primaryRuntime,
                StashOverlay: stashOverlay);
            CombinedScope = new WorkflowEvaluationScope(
                primaryPayload,
                environment,
                fallbackPayload,
                Runtime: combinedRuntime,
                StashOverlay: stashOverlay);

            _fallbackScope = new Lazy<WorkflowEvaluationScope?>(() =>
            {
                if (fallbackPayload is null)
                {
                    return null;
                }

                var fallbackRuntime = new WorkflowRuntimeEvaluationOverlay(
                    metadata.WorkflowType,
                    metadata.WorkflowInstanceId,
                    fallbackEvent,
                    fallbackEvent is not null);
                return new WorkflowEvaluationScope(
                    fallbackPayload,
                    environment,
                    Runtime: fallbackRuntime);
            });
        }

        internal JsonObject ExactRootPayload { get; }
        internal WorkflowEvaluationScope PrimaryScope { get; }
        internal WorkflowEvaluationScope CombinedScope { get; }

        internal static MappingResolutionContext Create(
            JsonObject primaryPayload,
            JsonObject? fallbackPayload,
            JsonObject exactRootPayload,
            IWorkflowContext? workflowContext,
            JsonObject? environment,
            JsonObject? stashOverlay = null)
            => new(
                primaryPayload,
                fallbackPayload,
                exactRootPayload,
                workflowContext,
                environment,
                stashOverlay);

        internal JsonNode? ResolveDirectPath(string path)
        {
            var selected = PrimaryScope.SelectToken(path);
            if (selected is not null)
            {
                return selected.DeepClone();
            }

            var fallback = _fallbackScope.Value;
            return fallback?.SelectToken(path)?.DeepClone();
        }

        internal JsonObject BuildLegacyPrimaryContext()
        {
            var primary = BuildContext(_primaryPayload, _workflowContext, _environment);
            if (_stashOverlay is not null)
            {
                if (primary["stash"] is not JsonObject stash)
                {
                    stash = new JsonObject();
                    primary["stash"] = stash;
                }

                foreach (var (key, value) in _stashOverlay)
                {
                    stash[key] = value?.DeepClone();
                }
            }

            return primary;
        }
    }

    private static void MergeInto(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            if (target[key] is JsonObject targetObject && value is JsonObject sourceObject)
            {
                MergeInto(targetObject, sourceObject);
                continue;
            }

            target[key] = value?.DeepClone();
        }
    }

    private static JsonObject? ResolveWorkflowEvent(JsonObject payload)
        => ResolveWorkflowEventView(payload) is { } workflowEvent
            ? (JsonObject)workflowEvent.DeepClone()
            : null;

    private static JsonObject? ResolveWorkflowEventView(JsonObject payload)
    {
        return ResolveWorkflowEventNodeView(payload["event"])
               ?? ResolveWorkflowEventNodeView(payload["resumeEvent"])
               ?? ResolveWorkflowEventNodeView(payload["__initiatingEvent"]);
    }

    private static JsonObject? ResolveWorkflowEventNodeView(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        return obj["Payload"] is JsonObject eventPayload
            ? eventPayload
            : obj;
    }
}
