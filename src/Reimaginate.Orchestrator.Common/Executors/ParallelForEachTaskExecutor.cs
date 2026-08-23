using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Agents.AI.Workflows;
using Reimaginate.Mediator;
using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.Common.Constants;
using Reimaginate.Orchestrator.Common.Diagnostics;
using Reimaginate.Orchestrator.Common.Helpers;
using Reimaginate.Orchestrator.Common.Models;
using Reimaginate.Orchestrator.Common.Services.WorkflowDefinitions;
using Reimaginate.Orchestrator.Common.Services.WorkflowDefinitions.Compilation;
using Reimaginate.Orchestrator.DSL;

namespace Reimaginate.Orchestrator.Common.Executors;

internal sealed class ParallelForEachTaskExecutor(
    string id,
    string locationHint,
    WorkflowMetadataDefinition? workflowMetadata,
    IReadOnlyDictionary<string, TaskDefinition> bodyTasks,
    IReadOnlyList<BoundWorkflowExtension> extensions,
    IMediator mediator,
    IWorkflowBuilderAdapterFactory workflowBuilderAdapterFactory,
    IWorkflowEventEmitter workflowEventEmitter,
    IWorkflowActionResolver workflowActionResolver,
    string sourceExpression,
    string itemVariable,
    string? indexVariable,
    int? batchSize,
    int maxConcurrency,
    string collectVariable,
    string? onError,
    JsonObject? inputMap = null,
    IReadOnlyList<string>? collectInclude = null,
    string? condition = null,
    JsonObject? environment = null,
    IWorkflowEnvironmentProvider? workflowEnvironmentProvider = null,
    string? workflowType = null) : Executor<JsonObject, JsonObject>(id)
{
    private const string FailFastMode = "failFast";
    private const string CollectMode = "collect";
    private readonly WorkflowCompilationPlan _childPlan = CompileChildPlan(id, workflowMetadata, bodyTasks, extensions);
    private readonly IReadOnlySet<string>? _collectFields = collectInclude?.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public override ValueTask<JsonObject> HandleAsync(JsonObject currentObject, IWorkflowContext context, CancellationToken cancellationToken = default)
        => WorkflowTaskExecutionGuard.RunAsync(Id, locationHint, () => HandleCoreAsync(currentObject, context, cancellationToken));

    private async ValueTask<JsonObject> HandleCoreAsync(JsonObject currentObject, IWorkflowContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!WorkflowConditionEvaluator.Evaluate(condition, currentObject, environment))
        {
            return (JsonObject)currentObject.DeepClone();
        }

        var output = (JsonObject)currentObject.DeepClone();
        var sourceItems = ResolveSourceItems(output);
        var iterations = BuildIterations(sourceItems);
        var results = new ParallelForEachIterationResult?[iterations.Count];
        var errorMode = NormalizeErrorMode(onError);

        await WorkflowExecutionTraceContext.RecordAsync("executor.parallel_foreach.started", new JsonObject
        {
            ["taskName"] = Id,
            ["iterationCount"] = iterations.Count,
            ["maxConcurrency"] = maxConcurrency,
            ["collect"] = collectVariable,
            ["onError"] = errorMode
        }, cancellationToken);

        if (iterations.Count == 0)
        {
            output[collectVariable] = new JsonArray();
            await WorkflowExecutionTraceContext.RecordAsync("executor.parallel_foreach.completed", new JsonObject
            {
                ["taskName"] = Id,
                ["iterationCount"] = 0,
                ["errorCount"] = 0
            }, cancellationToken);
            return output;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var failFastErrors = new ConcurrentQueue<ParallelForEachIterationException>();
        var nextIterationIndex = -1;

        cancellationToken.ThrowIfCancellationRequested();
        var workers = Enumerable.Range(0, Math.Min(maxConcurrency, iterations.Count))
            .Select(_ => ExecuteWorkerAsync())
            .ToArray();

        try
        {
            await Task.WhenAll(workers);
        }
        catch when (errorMode == FailFastMode && failFastErrors.TryPeek(out _))
        {
            // The first item failure is rethrown below with item context; cancellation noise from other tasks is ignored.
        }

        if (errorMode == FailFastMode && failFastErrors.TryPeek(out var firstError))
        {
            await WorkflowExecutionTraceContext.RecordAsync("executor.parallel_foreach.cancelled", new JsonObject
            {
                ["taskName"] = Id,
                ["iterationIndex"] = firstError.IterationIndex,
                ["message"] = firstError.Message
            }, cancellationToken);
            throw firstError;
        }

        var resultArray = new JsonArray();
        foreach (var result in results)
        {
            resultArray.Add((result ?? throw new InvalidOperationException($"Parallel for task '{Id}' did not produce a result for every iteration.")).ToJsonObject());
        }

        output[collectVariable] = resultArray;

        await WorkflowExecutionTraceContext.RecordAsync("executor.parallel_foreach.completed", new JsonObject
        {
            ["taskName"] = Id,
            ["iterationCount"] = iterations.Count,
            ["errorCount"] = results.Count(result => result is { Success: false })
        }, cancellationToken);

        return output;

        async Task ExecuteWorkerAsync()
        {
            while (true)
            {
                linkedCancellation.Token.ThrowIfCancellationRequested();
                var iterationIndex = Interlocked.Increment(ref nextIterationIndex);
                if (iterationIndex >= iterations.Count)
                {
                    return;
                }

                await ExecuteIterationAsync(
                    iterations[iterationIndex],
                    sourceItems!,
                    results,
                    output,
                    context,
                    errorMode,
                    linkedCancellation,
                    failFastErrors,
                    cancellationToken);
            }
        }
    }

    private async Task ExecuteIterationAsync(
        ParallelForEachIteration iteration,
        JsonArray sourceItems,
        ParallelForEachIterationResult?[] results,
        JsonObject parentPayload,
        IWorkflowContext workflowContext,
        string errorMode,
        CancellationTokenSource linkedCancellation,
        ConcurrentQueue<ParallelForEachIterationException> failFastErrors,
        CancellationToken traceCancellationToken)
    {
        var iterationValue = MaterializeIterationValue(sourceItems, iteration);
        try
        {
            linkedCancellation.Token.ThrowIfCancellationRequested();
            await WorkflowExecutionTraceContext.RecordAsync("executor.parallel_foreach.item_started", new JsonObject
            {
                ["taskName"] = Id,
                ["iterationIndex"] = iteration.Index
            }, traceCancellationToken);

            var childInput = BuildChildInput(parentPayload, iterationValue, iteration.Index, workflowContext);
            var childOutput = await ExecuteChildWorkflowAsync(childInput, linkedCancellation.Token);
            results[iteration.Index] = ParallelForEachIterationResult.SuccessResult(iteration.Index, iterationValue, childOutput, _collectFields);

            await WorkflowExecutionTraceContext.RecordAsync("executor.parallel_foreach.item_completed", new JsonObject
            {
                ["taskName"] = Id,
                ["iterationIndex"] = iteration.Index
            }, traceCancellationToken);
        }
        catch (Exception ex) when (errorMode == CollectMode && ex is not OperationCanceledException)
        {
            var itemError = UnwrapItemException(ex);
            results[iteration.Index] = ParallelForEachIterationResult.ErrorResult(
                iteration.Index,
                iterationValue,
                WorkflowErrorEnvelopeFactory.Create(itemError, Id),
                _collectFields);

            await WorkflowExecutionTraceContext.RecordAsync("executor.parallel_foreach.item_error", new JsonObject
            {
                ["taskName"] = Id,
                ["iterationIndex"] = iteration.Index,
                ["message"] = itemError.Message
            }, traceCancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var itemError = UnwrapItemException(ex);
            var wrapped = new ParallelForEachIterationException(Id, iteration.Index, itemError);
            failFastErrors.Enqueue(wrapped);
            linkedCancellation.Cancel();

            await WorkflowExecutionTraceContext.RecordAsync("executor.parallel_foreach.item_error", new JsonObject
            {
                ["taskName"] = Id,
                ["iterationIndex"] = iteration.Index,
                ["message"] = itemError.Message
            }, traceCancellationToken);

            throw wrapped;
        }
    }

    private async Task<JsonNode?> ExecuteChildWorkflowAsync(JsonObject childInput, CancellationToken cancellationToken)
    {
        var childWorkflow = BuildChildWorkflow();
        object? finalOutput = null;
        var sawOutput = false;

        await using var run = await InProcessExecution.RunStreamingAsync(childWorkflow, input: childInput, cancellationToken: cancellationToken);
        await foreach (var evt in run.WatchStreamAsync(cancellationToken))
        {
            switch (evt)
            {
                case WorkflowErrorEvent workflowErrorEvent:
                    throw workflowErrorEvent.Exception ?? new InvalidOperationException($"Parallel for child workflow failed for task '{Id}'.");
                case WorkflowOutputEvent outputEvent:
                    sawOutput = true;
                    if (outputEvent.Data is WorkflowTerminalResult terminalResult)
                    {
                        if (!string.Equals(terminalResult.Status, WorkflowInstanceStatuses.Completed, StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException($"Parallel for child workflow terminated with status '{terminalResult.Status}'.");
                        }

                        finalOutput = terminalResult.FinalOutput;
                    }
                    else
                    {
                        finalOutput = outputEvent.Data;
                    }

                    break;
            }
        }

        return sawOutput
            ? ToJsonNode(finalOutput)
            : childInput.DeepClone();
    }

    private Workflow BuildChildWorkflow()
    {
        var childChannel = Channel.CreateUnbounded<JsonObject>();
        return new WorkflowPlanMaterializer(mediator, workflowBuilderAdapterFactory, childChannel, workflowEventEmitter, workflowActionResolver, workflowEnvironmentProvider, workflowType)
            .Materialize(_childPlan);
    }

    private static WorkflowCompilationPlan CompileChildPlan(
        string taskId,
        WorkflowMetadataDefinition? metadata,
        IReadOnlyDictionary<string, TaskDefinition> tasks,
        IReadOnlyList<BoundWorkflowExtension> workflowExtensions)
    {
        var boundWorkflow = new BoundWorkflow(
            metadata,
            tasks.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
            tasks.Keys.ToArray(),
            workflowExtensions);

        var compilation = new WorkflowDefinitionCompiler().Compile(boundWorkflow);
        if (!compilation.IsSuccessful || compilation.Plan is null)
        {
            throw new InvalidOperationException($"Failed to compile parallel for child workflow for task '{taskId}': {string.Join("; ", compilation.Diagnostics.Select(diagnostic => diagnostic.Message))}");
        }

        return compilation.Plan;
    }

    private JsonArray? ResolveSourceItems(JsonObject payload)
        => JsonPath.SelectToken(payload, sourceExpression) as JsonArray;

    private List<ParallelForEachIteration> BuildIterations(JsonArray? sourceItems)
    {
        if (sourceItems is null || sourceItems.Count == 0)
        {
            return [];
        }

        if (batchSize is not > 0)
        {
            return sourceItems
                .Select((_, index) => new ParallelForEachIteration(index, index, 1, false))
                .ToList();
        }

        var iterations = new List<ParallelForEachIteration>();
        for (var startIndex = 0; startIndex < sourceItems.Count; startIndex += batchSize.Value)
        {
            var endExclusive = Math.Min(startIndex + batchSize.Value, sourceItems.Count);
            iterations.Add(new ParallelForEachIteration(iterations.Count, startIndex, endExclusive - startIndex, true));
        }

        return iterations;
    }

    private static JsonNode? MaterializeIterationValue(JsonArray sourceItems, ParallelForEachIteration iteration)
    {
        if (!iteration.IsBatch)
        {
            return sourceItems[iteration.StartIndex]?.DeepClone();
        }

        var chunk = new JsonArray();
        for (var index = iteration.StartIndex; index < iteration.StartIndex + iteration.Count; index++)
        {
            chunk.Add(sourceItems[index]?.DeepClone());
        }

        return chunk;
    }

    private JsonObject BuildChildInput(
        JsonObject parentPayload,
        JsonNode? iterationValue,
        int iterationIndex,
        IWorkflowContext workflowContext)
    {
        if (inputMap is not null)
        {
            if (TryBuildDirectLoopProjection(inputMap, parentPayload, iterationValue, iterationIndex, out var projected))
            {
                return projected;
            }

            var iterationContext = new JsonObject
            {
                [itemVariable] = iterationValue
            };
            if (!string.IsNullOrWhiteSpace(indexVariable))
            {
                iterationContext[indexVariable!] = iterationIndex;
            }

            return WorkflowMappingResolver.ResolveProjection(
                       inputMap,
                       iterationContext,
                       parentPayload,
                       iterationContext,
                       workflowContext,
                       environment);
        }

        var childInput = (JsonObject)parentPayload.DeepClone();
        childInput[itemVariable] = iterationValue?.DeepClone();
        if (!string.IsNullOrWhiteSpace(indexVariable))
        {
            childInput[indexVariable!] = iterationIndex;
        }

        return WorkflowStashHelper.MirrorRootValuesToStash(childInput);
    }

    private bool TryBuildDirectLoopProjection(
        JsonObject template,
        JsonObject parentPayload,
        JsonNode? iterationValue,
        int iterationIndex,
        out JsonObject projected)
    {
        var itemPath = "$." + itemVariable;
        var indexPath = string.IsNullOrWhiteSpace(indexVariable) ? null : "$." + indexVariable;
        var itemTransferred = false;
        if (!CanProject(template))
        {
            projected = null!;
            return false;
        }

        if (TryProject(template, out var result) && result is JsonObject resultObject)
        {
            projected = resultObject;
            return true;
        }

        projected = null!;
        return false;

        bool CanProject(JsonNode? node)
        {
            return node switch
            {
                null => true,
                JsonObject objectNode => objectNode.All(entry => CanProject(entry.Value)),
                JsonArray arrayNode => arrayNode.All(CanProject),
                JsonValue jsonValue when jsonValue.TryGetValue<string>(out var raw) => CanProjectString(raw),
                _ => true
            };
        }

        bool CanProjectString(string raw)
        {
            var trimmed = raw.Trim();
            return string.Equals(trimmed, itemPath, StringComparison.Ordinal)
                   || (indexPath is not null && string.Equals(trimmed, indexPath, StringComparison.Ordinal))
                   || trimmed.StartsWith(itemPath + ".", StringComparison.Ordinal)
                   || trimmed.StartsWith(itemPath + "[", StringComparison.Ordinal)
                   || (trimmed.StartsWith("$.", StringComparison.Ordinal)
                       && WorkflowMappingResolver.IsSimpleProjection(JsonValue.Create(raw)))
                   || (!trimmed.StartsWith("$", StringComparison.Ordinal)
                       && !trimmed.Contains("{{", StringComparison.Ordinal));
        }

        bool TryProject(JsonNode? node, out JsonNode? value)
        {
            switch (node)
            {
                case null:
                    value = null;
                    return true;
                case JsonObject objectNode:
                {
                    var objectResult = new JsonObject();
                    foreach (var (key, child) in objectNode)
                    {
                        if (!TryProject(child, out var projectedChild))
                        {
                            value = null;
                            return false;
                        }

                        objectResult[key] = projectedChild;
                    }

                    value = objectResult;
                    return true;
                }
                case JsonArray arrayNode:
                {
                    var arrayResult = new JsonArray();
                    foreach (var child in arrayNode)
                    {
                        if (!TryProject(child, out var projectedChild))
                        {
                            value = null;
                            return false;
                        }

                        arrayResult.Add(projectedChild);
                    }

                    value = arrayResult;
                    return true;
                }
                case JsonValue jsonValue when jsonValue.TryGetValue<string>(out var raw):
                {
                    var trimmed = raw.Trim();
                    if (string.Equals(trimmed, itemPath, StringComparison.Ordinal))
                    {
                        value = itemTransferred ? iterationValue?.DeepClone() : iterationValue;
                        itemTransferred = true;
                        return true;
                    }

                    if (indexPath is not null && string.Equals(trimmed, indexPath, StringComparison.Ordinal))
                    {
                        value = JsonValue.Create(iterationIndex);
                        return true;
                    }

                    if (trimmed.StartsWith(itemPath + ".", StringComparison.Ordinal)
                        || trimmed.StartsWith(itemPath + "[", StringComparison.Ordinal))
                    {
                        var itemRelativePath = "$" + trimmed[itemPath.Length..];
                        value = iterationValue is null
                            ? null
                            : JsonPath.SelectToken(iterationValue, itemRelativePath)?.DeepClone();
                        return true;
                    }

                    if (trimmed.StartsWith("$.", StringComparison.Ordinal)
                        && WorkflowMappingResolver.IsSimpleProjection(jsonValue))
                    {
                        value = JsonPath.SelectToken(parentPayload, trimmed)?.DeepClone();
                        return true;
                    }

                    if (trimmed.StartsWith("$", StringComparison.Ordinal)
                        || trimmed.Contains("{{", StringComparison.Ordinal))
                    {
                        value = null;
                        return false;
                    }

                    value = jsonValue.DeepClone();
                    return true;
                }
                default:
                    value = node.DeepClone();
                    return true;
            }
        }
    }

    private static string NormalizeErrorMode(string? configured)
        => string.Equals(configured, CollectMode, StringComparison.OrdinalIgnoreCase)
            ? CollectMode
            : FailFastMode;

    private static JsonNode? ToJsonNode(object? value)
    {
        return value switch
        {
            null => null,
            JsonNode node => node.DeepClone(),
            _ => JsonSerializer.SerializeToNode(value)
        };
    }

    private static Exception UnwrapItemException(Exception exception)
    {
        var current = exception;
        while (current is TargetInvocationException or WorkflowTaskExecutionException
               || current is AggregateException { InnerExceptions.Count: 1 })
        {
            current = current switch
            {
                TargetInvocationException { InnerException: not null } targetInvocationException => targetInvocationException.InnerException,
                WorkflowTaskExecutionException { InnerException: not null } taskExecutionException => taskExecutionException.InnerException,
                AggregateException { InnerExceptions.Count: 1 } aggregateException => aggregateException.InnerExceptions[0],
                _ => current
            };
        }

        return current ?? exception;
    }

    private sealed record ParallelForEachIteration(int Index, int StartIndex, int Count, bool IsBatch);

    private sealed record ParallelForEachIterationResult(
        int Index,
        bool Success,
        JsonNode? Item,
        JsonNode? Output,
        JsonObject? Error,
        IReadOnlySet<string>? IncludedFields)
    {
        public static ParallelForEachIterationResult SuccessResult(
            int index,
            JsonNode? item,
            JsonNode? output,
            IReadOnlySet<string>? includedFields)
            => new(
                index,
                true,
                Includes(includedFields, "item") ? item : null,
                Includes(includedFields, "output") ? output : null,
                null,
                includedFields);

        public static ParallelForEachIterationResult ErrorResult(
            int index,
            JsonNode? item,
            JsonObject error,
            IReadOnlySet<string>? includedFields)
            => new(
                index,
                false,
                Includes(includedFields, "item") ? item : null,
                null,
                Includes(includedFields, "error") ? error : null,
                includedFields);

        public JsonObject ToJsonObject()
        {
            var result = new JsonObject();
            if (Includes(IncludedFields, "index"))
            {
                result["index"] = Index;
            }

            if (Includes(IncludedFields, "success"))
            {
                result["success"] = Success;
            }

            if (Includes(IncludedFields, "item"))
            {
                result["item"] = Item?.DeepClone();
            }

            if (Includes(IncludedFields, "output"))
            {
                result["output"] = Output?.DeepClone();
            }

            if (Includes(IncludedFields, "error"))
            {
                result["error"] = Error?.DeepClone();
            }

            return result;
        }

        private static bool Includes(IReadOnlySet<string>? includedFields, string field)
            => includedFields is null || includedFields.Contains(field);
    }

    private sealed class ParallelForEachIterationException(string taskName, int iterationIndex, Exception innerException)
        : InvalidOperationException($"Parallel for task '{taskName}' failed while processing item index {iterationIndex}: {Describe(innerException)}", innerException)
    {
        public int IterationIndex { get; } = iterationIndex;

        private static string Describe(Exception exception)
            => exception is WorkflowRaisedException raised
                ? $"{raised.ErrorType}: {raised.Message}"
                : exception.Message;
    }
}
