using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Agents.AI.Workflows;
using Reimaginate.Mediator;
using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.Common.Executors;
using Reimaginate.Orchestrator.Common.Helpers;
using Reimaginate.Orchestrator.Common.Services.WorkflowDefinitions;
using Reimaginate.Orchestrator.DSL;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowDefinitions.Compilation;

internal sealed class WorkflowPlanMaterializer(
    IMediator mediator,
    IWorkflowBuilderAdapterFactory workflowBuilderAdapterFactory,
    Channel<JsonObject> channel,
    IWorkflowEventEmitter workflowEventEmitter,
    IWorkflowActionResolver workflowActionResolver,
    IWorkflowEnvironmentProvider? workflowEnvironmentProvider = null,
    string? workflowType = null)
{
    public Workflow Materialize(WorkflowCompilationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var executorType = typeof(RequestExecutor<,>);
        var executors = new Dictionary<string, ExecutorBinding>(StringComparer.Ordinal);
        var jsonExecutors = new Dictionary<string, Executor<JsonObject, JsonObject>>(StringComparer.Ordinal);
        var terminalExecutors = new Dictionary<string, Executor<JsonObject, object>>(StringComparer.Ordinal);
        var incomingTransitionCounts = plan.Transitions
            .GroupBy(transition => transition.ToTaskName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        foreach (var node in plan.Nodes.Values)
        {
            var effectiveWorkflowType = ResolveEffectiveWorkflowType(node);
            var environment = BuildEnvironment(effectiveWorkflowType);

            switch (node.Kind)
            {
                case WorkflowPlanNodeKind.Call:
                    if (string.IsNullOrWhiteSpace(node.CallTarget))
                    {
                        throw new InvalidOperationException($"Task '{node.Name}' at {node.LocationHint} is missing a callable target.");
                    }

                    var (requestType, responseType) = workflowActionResolver.Resolve(node.CallTarget);
                    var genericExecutorType = executorType.MakeGenericType(requestType, responseType);
                    var callExecutor = Activator.CreateInstance(genericExecutorType, node.Name, node.LocationHint, mediator, node.InputMap, node.OutputMap, node.OutputStashMap, node.Condition, node.CaptureErrors, null, environment)
                        as Executor<JsonObject, JsonObject>
                        ?? throw new InvalidOperationException($"Task '{node.Name}' could not be created as a JSON workflow executor.");
                    executors[node.Name] = callExecutor;
                    jsonExecutors[node.Name] = callExecutor;
                    break;

                case WorkflowPlanNodeKind.RunEntry:
                    var runExecutor = new RunWorkflowTaskExecutor(node.Name, node.LocationHint, node.InputMap, node.Condition, environment);
                    executors[node.Name] = runExecutor;
                    jsonExecutors[node.Name] = runExecutor;
                    break;

                case WorkflowPlanNodeKind.RunExit:
                    var runExitExecutor = new PassthroughTaskExecutor(node.Name, node.LocationHint, node.InputMap, node.OutputMap, node.OutputStashMap, node.Condition, environment);
                    executors[node.Name] = runExitExecutor;
                    jsonExecutors[node.Name] = runExitExecutor;
                    break;

                case WorkflowPlanNodeKind.Switch:
                    if (!plan.BranchesByTask.TryGetValue(node.Name, out var branches) || branches.Count == 0)
                    {
                        throw new InvalidOperationException($"Task '{node.Name}' at {node.LocationHint} does not define any switch branches.");
                    }

                    var routes = branches
                        .Select(branch => new SwitchRoute(branch.When, branch.RouteTaskName, branch.IsDefault, branch.LocationHint, branch.OutputMap, branch.OutputStashMap))
                        .ToList();
                    var switchExecutor = new SwitchTaskExecutor(node.Name, node.LocationHint, routes, environment);
                    executors[node.Name] = switchExecutor;
                    jsonExecutors[node.Name] = switchExecutor;
                    break;

                case WorkflowPlanNodeKind.Listen:
                    var listenDefinition = node.Definition as ListenTaskDefinition;
                    var listenTargets = (listenDefinition?.ToAny ?? [])
                        .Select(target => new ListenTaskExecutor.ListenTarget(
                            string.IsNullOrWhiteSpace(target.Type) ? "*" : target.Type,
                            string.IsNullOrWhiteSpace(target.Filter) ? (listenDefinition?.Filter ?? "true") : target.Filter))
                        .ToList();

                    if (listenTargets.Count == 0)
                    {
                        listenTargets.Add(new ListenTaskExecutor.ListenTarget("*", listenDefinition?.Filter ?? "true"));
                    }

                    var listenRead = listenDefinition?.Read;
                    var listenExecutor = new ListenTaskExecutor(channel, node.Name, node.LocationHint, listenTargets, listenRead, node.OutputMap, node.OutputStashMap, environment);
                    executors[node.Name] = listenExecutor;
                    jsonExecutors[node.Name] = listenExecutor;
                    break;

                case WorkflowPlanNodeKind.Wait:
                    var waitDefinition = node.Definition as WaitTaskDefinition;
                    var waitExecutor = new WaitTaskExecutor(node.Name, node.LocationHint, waitDefinition?.For, waitDefinition?.Until, node.OutputMap, node.OutputStashMap, node.Condition, environment);
                    executors[node.Name] = waitExecutor;
                    jsonExecutors[node.Name] = waitExecutor;
                    break;

                case WorkflowPlanNodeKind.Terminate:
                    var terminateExecutor = new TerminateTaskExecutor(node.Name, node.LocationHint, node.TerminalStatus, node.TerminalOutput, node.TerminalReason, node.Condition, environment);
                    executors[node.Name] = terminateExecutor;
                    terminalExecutors[node.Name] = terminateExecutor;
                    break;

                case WorkflowPlanNodeKind.Raise:
                    var raiseDefinition = node.Definition as RaiseTaskDefinition;
                    var raiseExecutor = new RaiseTaskExecutor(node.Name, node.LocationHint, raiseDefinition?.ErrorType ?? node.CallTarget, raiseDefinition?.Message, node.InputMap, node.Condition, node.CaptureErrors, environment);
                    executors[node.Name] = raiseExecutor;
                    jsonExecutors[node.Name] = raiseExecutor;
                    break;

                case WorkflowPlanNodeKind.Emit:
                    var emitDefinition = node.Definition as EmitTaskDefinition;
                    var emitExecutor = new EmitTaskExecutor(node.Name, node.LocationHint, workflowEventEmitter, emitDefinition, node.InputMap, node.OutputMap, node.OutputStashMap, node.Condition, node.CaptureErrors, environment);
                    executors[node.Name] = emitExecutor;
                    jsonExecutors[node.Name] = emitExecutor;
                    break;
                case WorkflowPlanNodeKind.Map:
                    var mapExecutor = new MapTaskExecutor(node.Name, node.LocationHint, node.MapPlan, node.OutputMap, node.OutputStashMap, node.Condition, environment);
                    executors[node.Name] = mapExecutor;
                    jsonExecutors[node.Name] = mapExecutor;
                    break;
                case WorkflowPlanNodeKind.Stash:
                    var stashExecutor = new StashTaskExecutor(node.Name, node.LocationHint, node.InputMap, node.Condition, environment);
                    executors[node.Name] = stashExecutor;
                    jsonExecutors[node.Name] = stashExecutor;
                    break;

                case WorkflowPlanNodeKind.DoEntry:
                case WorkflowPlanNodeKind.DoExit:
                case WorkflowPlanNodeKind.Passthrough:
                    var passthroughExecutor = new PassthroughTaskExecutor(node.Name, node.LocationHint, node.InputMap, node.OutputMap, node.OutputStashMap, node.Condition, environment);
                    executors[node.Name] = passthroughExecutor;
                    jsonExecutors[node.Name] = passthroughExecutor;
                    break;


                case WorkflowPlanNodeKind.ForkSplit:
                    var forkSplitExecutor = new PassthroughTaskExecutor(node.Name, node.LocationHint, node.InputMap, node.OutputMap, node.OutputStashMap, node.Condition, environment);
                    executors[node.Name] = forkSplitExecutor;
                    jsonExecutors[node.Name] = forkSplitExecutor;
                    break;

                case WorkflowPlanNodeKind.ForkJoin:
                    var requiredArrivals = incomingTransitionCounts.TryGetValue(node.Name, out var transitionCount)
                        ? Math.Max(1, transitionCount)
                        : 1;
                    var forkJoinExecutor = new ForkJoinTaskExecutor(node.Name, node.LocationHint, requiredArrivals, node.OutputMap, node.OutputStashMap, node.Condition, environment);
                    executors[node.Name] = forkJoinExecutor;
                    jsonExecutors[node.Name] = forkJoinExecutor;
                    break;
                case WorkflowPlanNodeKind.LoopGate:
                    if (node.DoLoopMode == WorkflowDoLoopMode.ForEach)
                    {
                        if (string.IsNullOrWhiteSpace(node.LoopSourceExpression) || string.IsNullOrWhiteSpace(node.LoopItemVariable))
                        {
                            throw new InvalidOperationException($"Loop task '{node.Name}' at {node.LocationHint} must define valid for.in and for.each values.");
                        }

                        var forEachExecutor = new ForEachLoopGateTaskExecutor(node.Name, node.LocationHint, node.LoopSourceExpression, node.LoopItemVariable, node.LoopIndexVariable, node.LoopBatchSize);
                        executors[node.Name] = forEachExecutor;
                        jsonExecutors[node.Name] = forEachExecutor;
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(node.Condition))
                    {
                        throw new InvalidOperationException($"Loop task '{node.Name}' at {node.LocationHint} must define a valid while condition.");
                    }

                    var loopPolicy = ResolveLoopGuardPolicy(node.LoopGuardPolicy);
                    var loopGateExecutor = new LoopGateTaskExecutor(node.Name, node.LocationHint, node.Condition, loopPolicy, environment);
                    executors[node.Name] = loopGateExecutor;
                    jsonExecutors[node.Name] = loopGateExecutor;
                    break;

                case WorkflowPlanNodeKind.ParallelForEach:
                    if (node.Definition is not DoTaskDefinition parallelDoTask)
                    {
                        throw new InvalidOperationException($"Parallel for task '{node.Name}' at {node.LocationHint} was expected to be a do task.");
                    }

                    if (string.IsNullOrWhiteSpace(node.LoopSourceExpression)
                        || string.IsNullOrWhiteSpace(node.LoopItemVariable)
                        || node.LoopMaxConcurrency is not > 0
                        || string.IsNullOrWhiteSpace(node.LoopCollectVariable))
                    {
                        throw new InvalidOperationException($"Parallel for task '{node.Name}' at {node.LocationHint} must define valid for.in, for.each, for.maxConcurrency, and for.collect values.");
                    }

                    var parallelForEachExecutor = new ParallelForEachTaskExecutor(
                        id: node.Name,
                        locationHint: node.LocationHint,
                        workflowMetadata: plan.Metadata,
                        bodyTasks: parallelDoTask.Do.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
                        extensions: plan.Extensions.Values.ToArray(),
                        mediator: mediator,
                        workflowBuilderAdapterFactory: workflowBuilderAdapterFactory,
                        workflowEventEmitter: workflowEventEmitter,
                        workflowActionResolver: workflowActionResolver,
                        sourceExpression: node.LoopSourceExpression,
                        itemVariable: node.LoopItemVariable,
                        indexVariable: node.LoopIndexVariable,
                        batchSize: node.LoopBatchSize,
                        maxConcurrency: node.LoopMaxConcurrency.Value,
                        collectVariable: node.LoopCollectVariable,
                        onError: node.LoopErrorMode,
                        inputMap: node.LoopInputMap,
                        collectInclude: node.LoopCollectInclude,
                        condition: node.Condition,
                        environment: environment,
                        workflowEnvironmentProvider: workflowEnvironmentProvider,
                        workflowType: effectiveWorkflowType);
                    executors[node.Name] = parallelForEachExecutor;
                    jsonExecutors[node.Name] = parallelForEachExecutor;
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported workflow node kind '{node.Kind}' for task '{node.Name}' at {node.LocationHint}.");
            }

            if (node.AppliedExtensions.Count > 0)
            {
                if (!WorkflowExtensionWrappingSupport.SupportsJsonHookWrapping(node.Kind))
                {
                    throw new InvalidOperationException($"Task '{node.Name}' at {node.LocationHint} cannot use extensions because its executor does not support JSON hook wrapping.");
                }

                if (!jsonExecutors.TryGetValue(node.Name, out var jsonExecutor))
                {
                    throw new InvalidOperationException($"Task '{node.Name}' at {node.LocationHint} could not be resolved as a JSON workflow executor.");
                }

                var resolvedExtensions = node.AppliedExtensions
                    .Select(extensionName => plan.Extensions.TryGetValue(extensionName, out var extension)
                        ? extension
                        : throw new InvalidOperationException($"Task '{node.Name}' at {node.LocationHint} references unknown extension '{extensionName}'."))
                    .ToArray();

                var wrappedExecutor = new WorkflowExtensionExecutor(
                    node.Name,
                    node.LocationHint,
                    jsonExecutor,
                    plan.Metadata,
                    resolvedExtensions,
                    mediator,
                    workflowBuilderAdapterFactory,
                    workflowEventEmitter,
                    workflowActionResolver,
                    environment,
                    workflowEnvironmentProvider,
                    ResolveEffectiveWorkflowType(node));

                executors[node.Name] = wrappedExecutor;
                jsonExecutors[node.Name] = wrappedExecutor;
            }

            if (!node.IsUserTask || string.IsNullOrWhiteSpace(node.ConsoleStepName))
            {
                continue;
            }

            if (jsonExecutors.TryGetValue(node.Name, out var authoredJsonExecutor))
            {
                var loggingExecutor = new WorkflowStepLoggingExecutor<JsonObject>(
                    node.Name,
                    node.ConsoleStepName,
                    authoredJsonExecutor);
                executors[node.Name] = loggingExecutor;
                jsonExecutors[node.Name] = loggingExecutor;
                continue;
            }

            if (node.Kind == WorkflowPlanNodeKind.Terminate
                && terminalExecutors.TryGetValue(node.Name, out var terminalExecutor))
            {
                var loggingExecutor = new WorkflowStepLoggingExecutor<object>(
                    node.Name,
                    node.ConsoleStepName,
                    terminalExecutor);
                executors[node.Name] = loggingExecutor;
                terminalExecutors[node.Name] = loggingExecutor;
            }
        }

        if (!executors.TryGetValue(plan.StartTaskName, out var startExecutor))
        {
            throw new InvalidOperationException($"Unable to resolve workflow start executor '{plan.StartTaskName}'.");
        }

        var workflowBuilder = workflowBuilderAdapterFactory.Create(startExecutor);
        var edgeBuilder = workflowBuilder.EdgeBuilder;

        foreach (var transition in plan.Transitions)
        {
            if (!executors.TryGetValue(transition.FromTaskName, out var fromExecutor) ||
                !executors.TryGetValue(transition.ToTaskName, out var toExecutor))
            {
                continue;
            }

            if (plan.Nodes.TryGetValue(transition.FromTaskName, out var fromNode)
                && fromNode.Kind == WorkflowPlanNodeKind.ForkJoin)
            {
                if (!workflowBuilder.SupportsRouteEdges)
                {
                    throw BuildUnsupportedRouteEdgeException(transition.FromTaskName, transition.ToTaskName, ForkJoinTaskExecutor.ContinueRoute);
                }

                edgeBuilder.AddRouteEdge(fromExecutor, toExecutor, ForkJoinTaskExecutor.ContinueRoute);
                continue;
            }

            if (plan.Nodes.TryGetValue(transition.FromTaskName, out fromNode)
                && fromNode.Kind == WorkflowPlanNodeKind.Listen)
            {
                if (!workflowBuilder.SupportsRouteEdges)
                {
                    throw BuildUnsupportedRouteEdgeException(transition.FromTaskName, transition.ToTaskName, ListenTaskExecutor.ContinueRoute);
                }

                edgeBuilder.AddRouteEdge(fromExecutor, toExecutor, ListenTaskExecutor.ContinueRoute);
                continue;
            }

            if (string.IsNullOrWhiteSpace(transition.RouteTaskName))
            {
                edgeBuilder.AddUnconditionalEdge(fromExecutor, toExecutor);

                continue;
            }

            if (!workflowBuilder.SupportsRouteEdges)
            {
                throw BuildUnsupportedRouteEdgeException(transition.FromTaskName, transition.ToTaskName, transition.RouteTaskName);
            }

            edgeBuilder.AddRouteEdge(fromExecutor, toExecutor, transition.RouteTaskName);
        }

        var reachableExecutors = executors
            .Values
            .Distinct()
            .ToArray();

        workflowBuilder.WithOutputFrom(reachableExecutors);

        return workflowBuilder.Build();
    }

    private JsonObject BuildEnvironment(string? effectiveWorkflowType)
        => workflowEnvironmentProvider?.BuildEnvironment(effectiveWorkflowType) ?? new JsonObject();

    private string? ResolveEffectiveWorkflowType(WorkflowPlanNode node)
        => !string.IsNullOrWhiteSpace(node.SourceWorkflowType)
           && node.Kind is not WorkflowPlanNodeKind.RunEntry
           && node.Kind is not WorkflowPlanNodeKind.RunExit
            ? node.SourceWorkflowType
            : workflowType;

    private static LoopGuardPolicy ResolveLoopGuardPolicy(WorkflowLoopGuardPolicy? configuredPolicy)
    {
        var defaults = LoopGuardPolicy.Default;
        return new LoopGuardPolicy(
            configuredPolicy?.MaxIterations is > 0 ? configuredPolicy.MaxIterations.Value : defaults.MaxIterations,
            configuredPolicy?.MaxRepeatedPayloads is > 0 ? configuredPolicy.MaxRepeatedPayloads.Value : defaults.MaxRepeatedPayloads,
            configuredPolicy?.Timeout,
            configuredPolicy?.CancelMode ?? defaults.CancelMode);
    }

    private static InvalidOperationException BuildUnsupportedRouteEdgeException(string fromTaskName, string toTaskName, string routeTaskName)
    {
        return new InvalidOperationException(
            $"Workflow edge from task '{fromTaskName}' to '{toTaskName}' requires route '{routeTaskName}', " +
            "but the selected workflow builder adapter does not support route-based edges.");
    }

}
