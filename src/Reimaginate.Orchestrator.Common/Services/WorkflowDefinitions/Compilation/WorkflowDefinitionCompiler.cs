using System.Text.Json;
using System.Text.Json.Nodes;
using Reimaginate.Orchestrator.Common.Executors;
using Reimaginate.Orchestrator.Common.Helpers;
using Reimaginate.Orchestrator.Common.Requests.Internal.BuildAgentWorkflow;
using Reimaginate.Orchestrator.DSL;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowDefinitions.Compilation;

internal sealed class WorkflowDefinitionCompiler
{
    public WorkflowCompilationResult Compile(BoundWorkflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var diagnostics = new List<WorkflowCompilationDiagnostic>();
        var definition = new WorkflowDefinition
        {
            Metadata = workflow.Metadata,
            Use = new WorkflowUseDefinition
            {
                Extensions = workflow.Extensions.Select(extension => new WorkflowExtensionDefinition
                {
                    Name = extension.Name,
                    Extend = extension.ExtendAll ? "all" : null,
                    Before = extension.Before,
                    BeforeWhen = extension.BeforeWhen,
                    After = extension.After,
                    AfterWhen = extension.AfterWhen,
                    OnError = extension.OnError,
                    OnErrorWhen = extension.OnErrorWhen
                }).ToList()
            },
            Do = workflow.Tasks.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal)
        };

        var graph = new WorkflowTaskGraphBuilder().Build(definition);
        var nodes = new Dictionary<string, WorkflowPlanNode>(StringComparer.Ordinal);
        var branchesByTask = new Dictionary<string, IReadOnlyList<WorkflowPlanBranch>>(StringComparer.Ordinal);
        var constraints = new List<WorkflowPlanConstraint>();

        foreach (var node in graph.Nodes.Values)
        {
            if (node.Kind == WorkflowTaskNodeKind.LoopGate)
            {
                ValidateLoopGate(node, diagnostics, constraints);
            }

            var planNode = BuildPlanNode(node);
            nodes[node.Name] = planNode;

            if (node.Kind != WorkflowTaskNodeKind.Switch)
            {
                continue;
            }

            if (node.Definition is not SwitchTaskDefinition switchTaskDefinition)
            {
                diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{node.Name}' at {node.LocationHint} was expected to be a switch task.", node.LocationHint));
                continue;
            }

            var branches = BuildSwitchBranches(node.Name, node.LocationHint, switchTaskDefinition, node.SwitchEndTargetName, graph.Nodes.Keys, diagnostics, constraints);
            branchesByTask[node.Name] = branches;
        }

        var transitions = BuildTransitions(graph, branchesByTask);

        if (diagnostics.Count > 0)
        {
            return new WorkflowCompilationResult(null, diagnostics);
        }

        var compiledExtensions = workflow.Extensions
            .Select(extension => CompileExtensionPlans(extension, workflow.Metadata))
            .ToDictionary(extension => extension.Name, extension => extension, StringComparer.Ordinal);

        var plan = new WorkflowCompilationPlan(
            workflow.Metadata,
            nodes,
            graph.RootTaskNames,
            graph.StartCandidateTaskName,
            transitions,
            branchesByTask,
            constraints,
            compiledExtensions);

        return new WorkflowCompilationResult(plan, diagnostics);
    }

    private static void ValidateLoopGate(
        WorkflowTaskNode node,
        ICollection<WorkflowCompilationDiagnostic> diagnostics,
        ICollection<WorkflowPlanConstraint> constraints)
    {
        if (node.LoopBodyTaskNames is null || node.LoopBodyTaskNames.Count == 0)
        {
            constraints.Add(new WorkflowPlanConstraint(
                WorkflowCompilationConstraintKind.DoWhileRequiresBody,
                node.Name,
                node.LocationHint,
                "Do loop must define at least one nested task."));
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Loop gate '{node.Name}' at {node.LocationHint} does not define any loop body tasks.", node.LocationHint));
        }

        var doTaskDefinition = node.Definition as DoTaskDefinition;
        if (!string.IsNullOrWhiteSpace(doTaskDefinition?.If) && !string.IsNullOrWhiteSpace(doTaskDefinition.Filter))
        {
            constraints.Add(new WorkflowPlanConstraint(
                WorkflowCompilationConstraintKind.DoTaskConflictingConditions,
                node.Name,
                node.LocationHint,
                "Do tasks support only one pre-condition field. Use either 'if' or 'filter'."));
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Loop gate '{node.Name}' at {node.LocationHint} defines both 'if' and 'filter'. Do tasks support only one pre-condition field.", node.LocationHint));
        }

        if (node.LoopMode == DoLoopMode.ForEach)
        {
            if (string.IsNullOrWhiteSpace(node.LoopSourceExpression) || string.IsNullOrWhiteSpace(node.LoopItemVariable))
            {
                diagnostics.Add(new WorkflowCompilationDiagnostic($"Loop gate '{node.Name}' at {node.LocationHint} must define non-empty for.in and for.each values.", node.LocationHint));
            }

            if (node.LoopBatchSize is <= 0)
            {
                diagnostics.Add(new WorkflowCompilationDiagnostic($"Loop gate '{node.Name}' at {node.LocationHint} must define a positive for.size value when provided.", node.LocationHint));
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(node.LoopCondition))
        {
            constraints.Add(new WorkflowPlanConstraint(
                WorkflowCompilationConstraintKind.DoWhileRequiresExpression,
                node.Name,
                node.LocationHint,
                "Do-while loop must define a non-empty while expression."));
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Loop gate '{node.Name}' at {node.LocationHint} must define a non-empty while expression.", node.LocationHint));
            return;
        }

        try
        {
            WorkflowConditionEvaluator.ValidateSyntax(node.LoopCondition, new JsonObject());
        }
        catch (Exception ex)
        {
            constraints.Add(new WorkflowPlanConstraint(
                WorkflowCompilationConstraintKind.DoWhileInvalidExpression,
                node.Name,
                node.LocationHint,
                "Do-while loop while expression is invalid and cannot be evaluated."));
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Loop gate '{node.Name}' at {node.LocationHint} has an invalid while expression: {ex.Message}", node.LocationHint));
        }
    }

    private static WorkflowPlanNode BuildPlanNode(WorkflowTaskNode node)
    {
        var planNode = node.Kind switch
        {
            WorkflowTaskNodeKind.Call => BuildCallNode(node),
            WorkflowTaskNodeKind.RunEntry => BuildRunEntryNode(node),
            WorkflowTaskNodeKind.RunExit => BuildRunExitNode(node),
            WorkflowTaskNodeKind.Switch => new WorkflowPlanNode(node.Name, WorkflowPlanNodeKind.Switch, node.LocationHint, node.Definition, node.NextTaskNames, node.IsUserTask, node.AppliedExtensions, node.SourceWorkflowType, node.ParentRunTaskName, null, null, null, null, null, CaptureErrors: node.CaptureErrors),
            WorkflowTaskNodeKind.Listen => BuildListenNode(node),
            WorkflowTaskNodeKind.Wait => BuildWaitNode(node),
            WorkflowTaskNodeKind.Terminate => BuildTerminateNode(node),
            WorkflowTaskNodeKind.Raise => BuildRaiseNode(node),
            WorkflowTaskNodeKind.Emit => BuildEmitNode(node),
            WorkflowTaskNodeKind.Map => BuildMapNode(node),
            WorkflowTaskNodeKind.Stash => BuildStashNode(node),
            WorkflowTaskNodeKind.DoEntry => BuildDoEntryNode(node),
            WorkflowTaskNodeKind.DoExit => BuildDoExitNode(node),
            WorkflowTaskNodeKind.Passthrough => BuildPassthroughNode(node),
            WorkflowTaskNodeKind.LoopGate => BuildLoopGateNode(node),
            WorkflowTaskNodeKind.ParallelForEach => BuildParallelForEachNode(node),
            WorkflowTaskNodeKind.ForkSplit => BuildForkSplitNode(node),
            WorkflowTaskNodeKind.ForkJoin => BuildForkJoinNode(node),
            _ => throw new InvalidOperationException($"Unsupported workflow node kind '{node.Kind}' for task '{node.Name}' at {node.LocationHint}.")
        };

        return planNode with { ConsoleStepName = node.ConsoleStepName };
    }

    private static WorkflowPlanNode BuildTerminateNode(WorkflowTaskNode node)
    {
        var terminateTask = node.Definition as TerminateTaskDefinition
            ?? throw new InvalidOperationException($"Task '{node.Name}' at {node.LocationHint} was expected to be a terminate task but was '{node.Definition?.GetType().Name ?? "unknown"}'.");

        return new WorkflowPlanNode(
            node.Name,
            WorkflowPlanNodeKind.Terminate,
            node.LocationHint,
            node.Definition,
            node.NextTaskNames,
            node.IsUserTask,
            node.AppliedExtensions,
            node.SourceWorkflowType,
            node.ParentRunTaskName,
            null,
            null,
            null,
            GetCondition(terminateTask),
            null,
            terminateTask.Status,
            BuildTerminalOutput(terminateTask.Output),
            terminateTask.Reason,
            CaptureErrors: node.CaptureErrors);
    }

    private static WorkflowPlanNode BuildLoopGateNode(WorkflowTaskNode node)
    {
        var doTask = node.Definition as DoTaskDefinition;
        var guardPolicy = BuildLoopGuardPolicy(doTask?.Guard);

        return new WorkflowPlanNode(
            node.Name,
            WorkflowPlanNodeKind.LoopGate,
            node.LocationHint,
            node.Definition,
            node.NextTaskNames,
            node.IsUserTask,
            node.AppliedExtensions,
            node.SourceWorkflowType,
            node.ParentRunTaskName,
            null,
            null,
            null,
            node.LoopCondition,
            null,
            DoLoopMode: node.LoopMode switch
            {
                DoLoopMode.ForEach => WorkflowDoLoopMode.ForEach,
                _ => WorkflowDoLoopMode.While
            },
            LoopSourceExpression: node.LoopSourceExpression,
            LoopItemVariable: node.LoopItemVariable,
            LoopIndexVariable: node.LoopIndexVariable,
            LoopBatchSize: node.LoopBatchSize,
            LoopGuardPolicy: guardPolicy,
            CaptureErrors: node.CaptureErrors);
    }

    private static WorkflowPlanNode BuildForkSplitNode(WorkflowTaskNode node)
    {
        var forkTask = node.Definition as ForkTaskDefinition;
        return new WorkflowPlanNode(
            node.Name,
            WorkflowPlanNodeKind.ForkSplit,
            node.LocationHint,
            node.Definition,
            node.NextTaskNames,
            node.IsUserTask,
            node.AppliedExtensions,
            node.SourceWorkflowType,
            node.ParentRunTaskName,
            null,
            null,
            null,
            GetCondition(forkTask ?? new ForkTaskDefinition()),
            null,
            CaptureErrors: node.CaptureErrors);
    }

    private static WorkflowPlanNode BuildParallelForEachNode(WorkflowTaskNode node)
    {
        return new WorkflowPlanNode(
            node.Name,
            WorkflowPlanNodeKind.ParallelForEach,
            node.LocationHint,
            node.Definition,
            node.NextTaskNames,
            node.IsUserTask,
            node.AppliedExtensions,
            node.SourceWorkflowType,
            node.ParentRunTaskName,
            null,
            null,
            null,
            node.Definition is null ? null : GetCondition(node.Definition),
            null,
            DoLoopMode: WorkflowDoLoopMode.ForEach,
            LoopSourceExpression: node.LoopSourceExpression,
            LoopItemVariable: node.LoopItemVariable,
            LoopIndexVariable: node.LoopIndexVariable,
            LoopBatchSize: node.LoopBatchSize,
            LoopMaxConcurrency: node.LoopMaxConcurrency,
            LoopInputMap: BuildInputMap(node.LoopInput),
            LoopCollectVariable: node.LoopCollectVariable,
            LoopCollectInclude: node.LoopCollectInclude,
            LoopErrorMode: node.LoopErrorMode,
            CaptureErrors: node.CaptureErrors);
    }

    private static BoundWorkflowExtension CompileExtensionPlans(
        BoundWorkflowExtension extension,
        WorkflowMetadataDefinition? workflowMetadata)
    {
        return extension with
        {
            BeforePlan = CompileHookPlan(extension.Name, "before", extension.Before, workflowMetadata),
            AfterPlan = CompileHookPlan(extension.Name, "after", extension.After, workflowMetadata),
            OnErrorPlan = CompileHookPlan(extension.Name, "onError", extension.OnError, workflowMetadata)
        };
    }

    private static WorkflowCompilationPlan? CompileHookPlan(
        string extensionName,
        string hookName,
        WorkflowTaskBlockDefinition? taskBlock,
        WorkflowMetadataDefinition? workflowMetadata)
    {
        if (taskBlock is null || taskBlock.Do.Count == 0)
        {
            return null;
        }

        var compilation = new WorkflowDefinitionCompiler().Compile(new BoundWorkflow(
            workflowMetadata,
            taskBlock.Do.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
            taskBlock.TaskOrder.ToArray(),
            []));

        if (!compilation.IsSuccessful || compilation.Plan is null)
        {
            throw new InvalidOperationException(
                $"Failed to compile extension '{extensionName}' {hookName} hook: {string.Join("; ", compilation.Diagnostics.Select(diagnostic => diagnostic.Message))}");
        }

        return compilation.Plan;
    }

    private static WorkflowPlanNode BuildForkJoinNode(WorkflowTaskNode node)
    {
        var forkTask = node.Definition as ForkTaskDefinition;
        return new WorkflowPlanNode(
            node.Name,
            WorkflowPlanNodeKind.ForkJoin,
            node.LocationHint,
            node.Definition,
            node.NextTaskNames,
            node.IsUserTask,
            node.AppliedExtensions,
            node.SourceWorkflowType,
            node.ParentRunTaskName,
            null,
            BuildOutputMap(forkTask?.Output?.As),
            BuildOutputStashMap(forkTask?.Output),
            GetCondition(forkTask ?? new ForkTaskDefinition()),
            null,
            CaptureErrors: node.CaptureErrors);
    }

    private static WorkflowLoopGuardPolicy? BuildLoopGuardPolicy(LoopGuardPolicyDefinition? guard)
    {
        if (guard is null)
        {
            return null;
        }

        var cancelMode = ParseCancelMode(guard.OnCancel);
        TimeSpan? timeout = guard.TimeoutMs is > 0 ? TimeSpan.FromMilliseconds(guard.TimeoutMs.Value) : null;
        return new WorkflowLoopGuardPolicy(
            guard.MaxIterations,
            guard.MaxRepeatedPayloads,
            timeout,
            cancelMode);
    }

    private static LoopGuardCancelMode? ParseCancelMode(string? onCancel)
    {
        if (string.IsNullOrWhiteSpace(onCancel))
        {
            return null;
        }

        return onCancel.Trim().ToLowerInvariant() switch
        {
            "fail" => LoopGuardCancelMode.Fail,
            "exit" => LoopGuardCancelMode.Exit,
            _ => null
        };
    }


    private static WorkflowPlanNode BuildDoEntryNode(WorkflowTaskNode node)
    {
        var inputMap = node.Definition is DoTaskDefinition doTask
            ? BuildInputMap(doTask.With)
            : null;

        return new WorkflowPlanNode(
            node.Name,
            WorkflowPlanNodeKind.DoEntry,
            node.LocationHint,
            node.Definition,
            node.NextTaskNames,
            node.IsUserTask,
            node.AppliedExtensions,
            node.SourceWorkflowType,
            node.ParentRunTaskName,
            inputMap,
            null,
            null,
            node.Definition is null ? null : GetCondition(node.Definition),
            null,
            CaptureErrors: node.CaptureErrors);
    }

    private static WorkflowPlanNode BuildDoExitNode(WorkflowTaskNode node)
    {
        var outputMap = node.Definition is DoTaskDefinition doTask
            ? BuildOutputMap(doTask.Output?.As)
            : null;
        var outputStashMap = node.Definition is DoTaskDefinition doTaskWithOutput
            ? BuildOutputStashMap(doTaskWithOutput.Output)
            : null;

        return new WorkflowPlanNode(
            node.Name,
            WorkflowPlanNodeKind.DoExit,
            node.LocationHint,
            node.Definition,
            node.NextTaskNames,
            node.IsUserTask,
            node.AppliedExtensions,
            node.SourceWorkflowType,
            node.ParentRunTaskName,
            null,
            outputMap,
            outputStashMap,
            node.Definition is null ? null : GetCondition(node.Definition),
            null,
            CaptureErrors: node.CaptureErrors);
    }

    private static WorkflowPlanNode BuildPassthroughNode(WorkflowTaskNode node)
    {
        return new WorkflowPlanNode(
            node.Name,
            WorkflowPlanNodeKind.Passthrough,
            node.LocationHint,
            node.Definition,
            node.NextTaskNames,
            node.IsUserTask,
            node.AppliedExtensions,
            node.SourceWorkflowType,
            node.ParentRunTaskName,
            null,
            null,
            null,
            node.Definition is null ? null : GetCondition(node.Definition),
            null,
            CaptureErrors: node.CaptureErrors);
    }

    private static WorkflowPlanNode BuildRunEntryNode(WorkflowTaskNode node)
    {
        var runTask = node.Definition as RunTaskDefinition
            ?? throw new InvalidOperationException($"Task '{node.Name}' at {node.LocationHint} was expected to be a run task but was '{node.Definition?.GetType().Name ?? "unknown"}'.");

        return new WorkflowPlanNode(
            node.Name,
            WorkflowPlanNodeKind.RunEntry,
            node.LocationHint,
            node.Definition,
            node.NextTaskNames,
            node.IsUserTask,
            node.AppliedExtensions,
            node.SourceWorkflowType,
            node.ParentRunTaskName,
            BuildInputMap(runTask.Workflow.Input),
            null,
            null,
            GetCondition(runTask),
            runTask.Workflow.WorkflowType,
            CaptureErrors: node.CaptureErrors);
    }

    private static WorkflowPlanNode BuildRunExitNode(WorkflowTaskNode node)
    {
        var runTask = node.Definition as RunTaskDefinition
            ?? throw new InvalidOperationException($"Task '{node.Name}' at {node.LocationHint} was expected to be a run task but was '{node.Definition?.GetType().Name ?? "unknown"}'.");

        return new WorkflowPlanNode(
            node.Name,
            WorkflowPlanNodeKind.RunExit,
            node.LocationHint,
            node.Definition,
            node.NextTaskNames,
            node.IsUserTask,
            node.AppliedExtensions,
            node.SourceWorkflowType,
            node.ParentRunTaskName,
            null,
            BuildOutputMap(runTask.Output?.As),
            BuildOutputStashMap(runTask.Output),
            null,
            runTask.Workflow.WorkflowType,
            CaptureErrors: node.CaptureErrors);
    }

    private static WorkflowPlanNode BuildCallNode(WorkflowTaskNode node)
    {
        var callTask = node.Definition as CallTaskDefinition
            ?? throw new InvalidOperationException($"Task '{node.Name}' at {node.LocationHint} was expected to be a call task but was '{node.Definition?.GetType().Name ?? "unknown"}'.");

        return new WorkflowPlanNode(
            node.Name,
            WorkflowPlanNodeKind.Call,
            node.LocationHint,
            node.Definition,
            node.NextTaskNames,
            node.IsUserTask,
            node.AppliedExtensions,
            node.SourceWorkflowType,
            node.ParentRunTaskName,
            BuildInputMap(callTask.With),
            BuildOutputMap(callTask.Output?.As),
            BuildOutputStashMap(callTask.Output),
            GetCondition(callTask),
            callTask.Call,
            CaptureErrors: node.CaptureErrors);
    }

    private static WorkflowPlanNode BuildListenNode(WorkflowTaskNode node)
    {
        var listenTask = node.Definition as ListenTaskDefinition
            ?? throw new InvalidOperationException($"Task '{node.Name}' at {node.LocationHint} was expected to be a listen task but was '{node.Definition?.GetType().Name ?? "unknown"}'.");

        return new WorkflowPlanNode(
            node.Name,
            WorkflowPlanNodeKind.Listen,
            node.LocationHint,
            node.Definition,
            node.NextTaskNames,
            node.IsUserTask,
            node.AppliedExtensions,
            node.SourceWorkflowType,
            node.ParentRunTaskName,
            null,
            BuildOutputMap(listenTask.Output?.As),
            BuildOutputStashMap(listenTask.Output),
            GetCondition(listenTask),
            null,
            CaptureErrors: node.CaptureErrors);
    }



    private static WorkflowPlanNode BuildRaiseNode(WorkflowTaskNode node)
    {
        var raiseTask = node.Definition as RaiseTaskDefinition
            ?? throw new InvalidOperationException($"Task '{node.Name}' at {node.LocationHint} was expected to be a raise task but was '{node.Definition?.GetType().Name ?? "unknown"}'.");

        return new WorkflowPlanNode(
            node.Name,
            WorkflowPlanNodeKind.Raise,
            node.LocationHint,
            node.Definition,
            node.NextTaskNames,
            node.IsUserTask,
            node.AppliedExtensions,
            node.SourceWorkflowType,
            node.ParentRunTaskName,
            BuildInputMap(raiseTask.Data),
            null,
            null,
            GetCondition(raiseTask),
            raiseTask.ErrorType,
            CaptureErrors: node.CaptureErrors);
    }


    private static WorkflowPlanNode BuildEmitNode(WorkflowTaskNode node)
    {
        var emitTask = node.Definition as EmitTaskDefinition
            ?? throw new InvalidOperationException($"Task '{node.Name}' at {node.LocationHint} was expected to be an emit task but was '{node.Definition?.GetType().Name ?? "unknown"}'.");

        return new WorkflowPlanNode(
            node.Name,
            WorkflowPlanNodeKind.Emit,
            node.LocationHint,
            node.Definition,
            node.NextTaskNames,
            node.IsUserTask,
            node.AppliedExtensions,
            node.SourceWorkflowType,
            node.ParentRunTaskName,
            BuildInputMap(emitTask.Data),
            BuildOutputMap(emitTask.Output?.As),
            BuildOutputStashMap(emitTask.Output),
            GetCondition(emitTask),
            emitTask.EventType,
            CaptureErrors: node.CaptureErrors);
    }

    private static WorkflowPlanNode BuildMapNode(WorkflowTaskNode node)
    {
        var mapTask = node.Definition as MapTaskDefinition
            ?? throw new InvalidOperationException($"Task '{node.Name}' at {node.LocationHint} was expected to be a map task but was '{node.Definition?.GetType().Name ?? "unknown"}'.");

        return new WorkflowPlanNode(
            node.Name,
            WorkflowPlanNodeKind.Map,
            node.LocationHint,
            node.Definition,
            node.NextTaskNames,
            node.IsUserTask,
            node.AppliedExtensions,
            node.SourceWorkflowType,
            node.ParentRunTaskName,
            null,
            BuildOutputMap(mapTask.Output?.As),
            BuildOutputStashMap(mapTask.Output),
            GetCondition(mapTask),
            null,
            CaptureErrors: node.CaptureErrors,
            MapPlan: WorkflowMapPlanCompiler.Compile(mapTask.Map));
    }

    private static WorkflowPlanNode BuildStashNode(WorkflowTaskNode node)
    {
        var stashTask = node.Definition as StashTaskDefinition
            ?? throw new InvalidOperationException($"Task '{node.Name}' at {node.LocationHint} was expected to be a stash task but was '{node.Definition?.GetType().Name ?? "unknown"}'.");

        return new WorkflowPlanNode(
            node.Name,
            WorkflowPlanNodeKind.Stash,
            node.LocationHint,
            node.Definition,
            node.NextTaskNames,
            node.IsUserTask,
            node.AppliedExtensions,
            node.SourceWorkflowType,
            node.ParentRunTaskName,
            BuildInputMap(stashTask.Values),
            null,
            null,
            GetCondition(stashTask),
            null,
            CaptureErrors: node.CaptureErrors);
    }

    private static WorkflowPlanNode BuildWaitNode(WorkflowTaskNode node)
    {
        var waitTask = node.Definition as WaitTaskDefinition
            ?? throw new InvalidOperationException($"Task '{node.Name}' at {node.LocationHint} was expected to be a wait task but was '{node.Definition?.GetType().Name ?? "unknown"}'.");

        return new WorkflowPlanNode(
            node.Name,
            WorkflowPlanNodeKind.Wait,
            node.LocationHint,
            node.Definition,
            node.NextTaskNames,
            node.IsUserTask,
            node.AppliedExtensions,
            node.SourceWorkflowType,
            node.ParentRunTaskName,
            null,
            BuildOutputMap(waitTask.Output?.As),
            BuildOutputStashMap(waitTask.Output),
            GetCondition(waitTask),
            null,
            CaptureErrors: node.CaptureErrors);
    }


    private static IReadOnlyList<WorkflowPlanBranch> BuildSwitchBranches(
        string taskName,
        string locationHint,
        SwitchTaskDefinition switchTaskDefinition,
        string? switchEndTargetName,
        IEnumerable<string> definedNodeNames,
        ICollection<WorkflowCompilationDiagnostic> diagnostics,
        ICollection<WorkflowPlanConstraint> constraints)
    {
        var branches = SwitchTaskAdapter.ReadBranches(switchTaskDefinition, locationHint);
        var routes = new List<WorkflowPlanBranch>(branches.Count);

        foreach (var branch in branches)
        {
            if (SwitchTaskAdapter.IsEndDirective(branch.Then))
            {
                var endTargetName = string.IsNullOrWhiteSpace(switchEndTargetName) ? null : switchEndTargetName;
                routes.Add(new WorkflowPlanBranch(
                    taskName,
                    branch.When,
                    endTargetName,
                    endTargetName ?? SwitchTaskAdapter.EndDirective,
                    branch.IsDefault,
                    branch.LocationHint,
                    BuildOutputMap(branch.Output?.As),
                    BuildOutputStashMap(branch.Output)));
                continue;
            }

            if (TryGetSingleNamedTarget(branch.Then, taskName, branch.LocationHint) is not { } targetName)
            {
                constraints.Add(new WorkflowPlanConstraint(
                    WorkflowCompilationConstraintKind.SwitchBranchRequiresSingleNamedTarget,
                    taskName,
                    branch.LocationHint,
                    "Each switch branch must define exactly one named then target."));
                diagnostics.Add(new WorkflowCompilationDiagnostic(
                    $"Task '{taskName}' at {branch.LocationHint} uses an unsupported switch branch. Each switch branch must define a single named 'then' task.",
                    branch.LocationHint));
                continue;
            }

            routes.Add(new WorkflowPlanBranch(
                taskName,
                branch.When,
                targetName,
                targetName,
                branch.IsDefault,
                branch.LocationHint,
                BuildOutputMap(branch.Output?.As),
                BuildOutputStashMap(branch.Output)));
        }

        if (routes.Count == 0)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint} does not define any switch branches.", locationHint));
            return routes;
        }

        var defaultCount = routes.Count(branch => branch.IsDefault);
        if (defaultCount > 1)
        {
            constraints.Add(new WorkflowPlanConstraint(
                WorkflowCompilationConstraintKind.SwitchSupportsSingleDefault,
                taskName,
                locationHint,
                "Switch tasks support at most one default branch."));
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint} defines more than one default switch branch.", locationHint));
        }

        var nodeNames = new HashSet<string>(definedNodeNames, StringComparer.Ordinal);
        foreach (var route in routes)
        {
            if (route.TargetTaskName is null)
            {
                continue;
            }

            if (nodeNames.Contains(route.TargetTaskName))
            {
                continue;
            }

            diagnostics.Add(new WorkflowCompilationDiagnostic(
                $"Switch task '{taskName}' references one or more unavailable target tasks: {route.TargetTaskName}.",
                route.LocationHint));
        }

        return routes;
    }

    private static IReadOnlyList<WorkflowPlanTransition> BuildTransitions(
        WorkflowTaskGraph graph,
        IReadOnlyDictionary<string, IReadOnlyList<WorkflowPlanBranch>> branchesByTask)
    {
        var transitions = new List<WorkflowPlanTransition>();

        foreach (var edge in graph.Adjacency)
        {
            if (!graph.Nodes.TryGetValue(edge.Key, out var node))
            {
                continue;
            }

            if (node.Kind == WorkflowTaskNodeKind.Switch)
            {
                if (!branchesByTask.TryGetValue(edge.Key, out var branches))
                {
                    continue;
                }

                var emittedTargets = new HashSet<string>(StringComparer.Ordinal);
                foreach (var branch in branches)
                {
                    if (branch.TargetTaskName is null || !emittedTargets.Add(branch.RouteTaskName))
                    {
                        continue;
                    }

                    transitions.Add(new WorkflowPlanTransition(edge.Key, branch.TargetTaskName, branch.LocationHint, branch.RouteTaskName));
                }

                continue;
            }

            if (node.Kind == WorkflowTaskNodeKind.LoopGate)
            {
                var continueRoute = node.LoopMode == DoLoopMode.ForEach
                    ? ForEachLoopGateTaskExecutor.ContinueRoute
                    : LoopGateTaskExecutor.ContinueRoute;
                var exitRoute = node.LoopMode == DoLoopMode.ForEach
                    ? ForEachLoopGateTaskExecutor.ExitRoute
                    : LoopGateTaskExecutor.ExitRoute;

                foreach (var nextTaskName in node.LoopBodyTaskNames ?? [])
                {
                    transitions.Add(new WorkflowPlanTransition(edge.Key, nextTaskName, node.LocationHint, continueRoute));
                }

                foreach (var nextTaskName in node.LoopExitTaskNames ?? [])
                {
                    transitions.Add(new WorkflowPlanTransition(edge.Key, nextTaskName, node.LocationHint, exitRoute));
                }

                continue;
            }

            if (node.Kind == WorkflowTaskNodeKind.DoEntry
                && node.LocationHint.EndsWith(".do-entry", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var nextTaskName in edge.Value)
                {
                    var routeTaskName = graph.Nodes.TryGetValue(nextTaskName, out var nextNode)
                        && nextNode.LocationHint.EndsWith(".do-exit", StringComparison.OrdinalIgnoreCase)
                            ? DoControlFlowDirectiveTaskExecutor.ExitRoute
                            : DoControlFlowDirectiveTaskExecutor.ContinueRoute;

                    transitions.Add(new WorkflowPlanTransition(edge.Key, nextTaskName, node.LocationHint, routeTaskName));
                }

                continue;
            }

            if (node.Kind == WorkflowTaskNodeKind.RunEntry
                && node.LocationHint.EndsWith(".run-entry", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var nextTaskName in edge.Value)
                {
                    var routeTaskName = graph.Nodes.TryGetValue(nextTaskName, out var nextNode)
                        && nextNode.LocationHint.EndsWith(".run-exit", StringComparison.OrdinalIgnoreCase)
                            ? DoControlFlowDirectiveTaskExecutor.ExitRoute
                            : DoControlFlowDirectiveTaskExecutor.ContinueRoute;

                    transitions.Add(new WorkflowPlanTransition(edge.Key, nextTaskName, node.LocationHint, routeTaskName));
                }

                continue;
            }

            foreach (var nextTaskName in edge.Value)
            {
                transitions.Add(new WorkflowPlanTransition(edge.Key, nextTaskName, node.LocationHint));
            }
        }

        return transitions;
    }

    private static string? TryGetSingleNamedTarget(object? thenValue, string taskName, string locationHint)
    {
        var normalizer = new ThenTargetNormalizer((parentName, displayName) => $"{parentName}.__validation_{displayName}");
        var normalizedTargets = normalizer.Normalize(thenValue, taskName, locationHint).ToList();

        return normalizedTargets.Count == 1 && normalizedTargets[0] is NamedTaskTarget target
            ? target.Name
            : null;
    }

    private static JsonObject? BuildInputMap(IDictionary<string, object>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return null;
        }

        return JsonSerializer.SerializeToNode(arguments) as JsonObject;
    }

    private static JsonObject? BuildOutputMap(object? outputDefinition)
    {
        if (outputDefinition is null)
        {
            return null;
        }

        return JsonSerializer.SerializeToNode(outputDefinition) as JsonObject;
    }

    private static JsonNode? BuildTerminalOutput(object? outputDefinition)
    {
        return outputDefinition is null
            ? null
            : JsonSerializer.SerializeToNode(outputDefinition);
    }

    private static JsonObject? BuildOutputStashMap(TaskOutputDefinition? outputDefinition)
    {
        if (outputDefinition?.Stash is null || outputDefinition.Stash.Count == 0)
        {
            return null;
        }

        return JsonSerializer.SerializeToNode(outputDefinition.Stash) as JsonObject;
    }

    private static string? GetCondition(TaskDefinition taskDefinition)
    {
        var ifProperty = taskDefinition.GetType().GetProperty("If");
        var ifCondition = ReadConditionValue(ifProperty?.GetValue(taskDefinition));
        if (!string.IsNullOrWhiteSpace(ifCondition))
        {
            return ifCondition;
        }

        var filterProperty = taskDefinition.GetType().GetProperty("Filter");
        var filterCondition = ReadConditionValue(filterProperty?.GetValue(taskDefinition));
        if (!string.IsNullOrWhiteSpace(filterCondition))
        {
            return filterCondition;
        }

        return null;
    }

    private static string? ReadConditionValue(object? rawCondition)
    {
        if (rawCondition is null)
        {
            return null;
        }

        return rawCondition switch
        {
            string text => string.IsNullOrWhiteSpace(text) ? null : text,
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonNode node => node.ToJsonString(),
            _ => rawCondition.ToString()
        };
    }
}
