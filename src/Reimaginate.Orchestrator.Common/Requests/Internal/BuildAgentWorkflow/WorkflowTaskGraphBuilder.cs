using Reimaginate.Orchestrator.DSL;
using Reimaginate.Orchestrator.Common.Services.WorkflowDefinitions;

namespace Reimaginate.Orchestrator.Common.Requests.Internal.BuildAgentWorkflow;

internal enum WorkflowTaskNodeKind
{
    Call,
    RunEntry,
    RunExit,
    Switch,
    Listen,
    Wait,
    Terminate,
    Raise,
    Emit,
    Map,
    Stash,
    DoEntry,
    DoExit,
    Passthrough,
    LoopGate,
    ParallelForEach,
    ForkSplit,
    ForkJoin
}

internal enum DoLoopMode
{
    While,
    ForEach
}

internal sealed record WorkflowTaskNode(
    string Name,
    WorkflowTaskNodeKind Kind,
    string LocationHint,
    TaskDefinition? Definition,
    IReadOnlyList<string> NextTaskNames,
    bool IsUserTask,
    IReadOnlyList<string> AppliedExtensions,
    string? SourceWorkflowType = null,
    string? ParentRunTaskName = null,
    DoLoopMode? LoopMode = null,
    string? LoopCondition = null,
    string? LoopSourceExpression = null,
    string? LoopItemVariable = null,
    string? LoopIndexVariable = null,
    int? LoopBatchSize = null,
    int? LoopMaxConcurrency = null,
    IDictionary<string, object>? LoopInput = null,
    string? LoopCollectVariable = null,
    IReadOnlyList<string>? LoopCollectInclude = null,
    string? LoopErrorMode = null,
    IReadOnlyList<string>? LoopBodyTaskNames = null,
    IReadOnlyList<string>? LoopExitTaskNames = null,
    string? SwitchEndTargetName = null,
    bool CaptureErrors = false,
    string? ConsoleStepName = null);

internal sealed record WorkflowTaskGraph(
    IReadOnlyDictionary<string, WorkflowTaskNode> Nodes,
    IReadOnlyList<string> RootTaskNames,
    string StartCandidateTaskName,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Adjacency);

internal sealed record WorkflowSwitchBranch(
    string? When,
    TaskOutputDefinition? Output,
    object? Then,
    bool IsDefault,
    string LocationHint);

internal sealed class WorkflowTaskGraphBuilder
{
    private sealed class InternalNodeIdGenerator
    {
        private int _counter;

        public string Next(string parentTaskName, string displayName)
        {
            _counter++;
            return $"{parentTaskName}.__internal_{_counter:D3}_{displayName}";
        }
    }

    public WorkflowTaskGraph Build(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var nodeRegistry = new Dictionary<string, WorkflowTaskNode>(StringComparer.Ordinal);
        var rootTaskNames = new List<string>();
        var internalNodeIds = new InternalNodeIdGenerator();
        var globalExtensions = definition.Use?.Extensions
            .Where(extension => string.Equals(extension.Extend, "all", StringComparison.OrdinalIgnoreCase))
            .Select(extension => extension.Name)
            .ToArray() ?? [];

        foreach (var taskEntry in definition.Do)
        {
            var taskName = taskEntry.Key;
            var locationHint = BuildLocationHint(taskName);
            rootTaskNames.Add(taskName);

            RegisterTaskNode(taskName, taskEntry.Value, locationHint, nodeRegistry, internalNodeIds, globalExtensions);
        }

        var startCandidateTaskName = nodeRegistry.ContainsKey("start")
            ? "start"
            : rootTaskNames[0];

        var adjacency = nodeRegistry.Values.ToDictionary(
            node => node.Name,
            node => (IReadOnlyList<string>)node.NextTaskNames,
            StringComparer.Ordinal);

        return new WorkflowTaskGraph(nodeRegistry, rootTaskNames, startCandidateTaskName, adjacency);
    }

    private static WorkflowTaskNode RegisterTaskNode(
        string taskName,
        TaskDefinition taskDefinition,
        string locationHint,
        IDictionary<string, WorkflowTaskNode> nodeRegistry,
        InternalNodeIdGenerator internalNodeIds,
        IReadOnlyList<string> globalExtensions,
        string? switchEndTargetName = null,
        bool captureErrors = false)
    {
        if (nodeRegistry.TryGetValue(taskName, out var existingNode))
        {
            return existingNode;
        }

        var nodeKind = GetNodeKind(taskDefinition, taskName, locationHint);
        var nextTaskNames = GetNextTaskNames(taskDefinition, taskName, locationHint, nodeRegistry, internalNodeIds, globalExtensions, switchEndTargetName, captureErrors);
        var appliedGlobalExtensions = WorkflowExtensionWrappingSupport.SupportsJsonHookWrapping(taskDefinition)
            ? globalExtensions
            : [];

        var node = new WorkflowTaskNode(
            taskName,
            nodeKind,
            locationHint,
            taskDefinition,
            nextTaskNames,
            true,
            CombineExtensions(appliedGlobalExtensions, taskDefinition.Extend),
            SwitchEndTargetName: nodeKind == WorkflowTaskNodeKind.Switch ? switchEndTargetName : null,
            CaptureErrors: captureErrors,
            ConsoleStepName: GetAuthoredTaskName(taskName));

        nodeRegistry[taskName] = node;
        return node;
    }

    private static IReadOnlyList<string> GetNextTaskNames(
        TaskDefinition taskDefinition,
        string taskName,
        string locationHint,
        IDictionary<string, WorkflowTaskNode> nodeRegistry,
        InternalNodeIdGenerator internalNodeIds,
        IReadOnlyList<string> globalExtensions,
        string? switchEndTargetName,
        bool captureErrors)
    {
        var nextTasks = new List<string>();

        if (taskDefinition is DoTaskDefinition doTaskDefinition)
        {
            return BuildDoTaskNodes(doTaskDefinition, taskName, locationHint, nodeRegistry, internalNodeIds, globalExtensions);
        }

        if (taskDefinition is RunTaskDefinition runTaskDefinition)
        {
            return BuildRunTaskNodes(runTaskDefinition, taskName, locationHint, nodeRegistry, internalNodeIds, globalExtensions);
        }

        if (taskDefinition is TryTaskDefinition tryTaskDefinition)
        {
            return BuildTryTaskNodes(tryTaskDefinition, taskName, locationHint, nodeRegistry, internalNodeIds, globalExtensions);
        }

        if (taskDefinition is TerminateTaskDefinition)
        {
            return [];
        }

        if (taskDefinition is ForkTaskDefinition forkTaskDefinition)
        {
            return BuildForkTaskNodes(forkTaskDefinition, taskName, locationHint, nodeRegistry, internalNodeIds, globalExtensions);
        }

        AddThenTargets(taskDefinition.Then, taskName, locationHint, nextTasks, nodeRegistry, internalNodeIds);

        if (taskDefinition is SwitchTaskDefinition switchTaskDefinition)
        {
            AddSwitchTargets(switchTaskDefinition, taskName, locationHint, nextTasks, nodeRegistry, internalNodeIds, switchEndTargetName);
        }

        return nextTasks;
    }


    private static IReadOnlyList<string> BuildDoTaskNodes(
        DoTaskDefinition doTaskDefinition,
        string taskName,
        string locationHint,
        IDictionary<string, WorkflowTaskNode> nodeRegistry,
        InternalNodeIdGenerator internalNodeIds,
        IReadOnlyList<string> globalExtensions)
    {
        var doEntryTaskName = internalNodeIds.Next(taskName, "do_entry");
        var doExitTaskName = internalNodeIds.Next(taskName, "do_exit");
        var doTaskNextTasks = new List<string> { doEntryTaskName };
        var doEntryNextTasks = new List<string>();
        var doExitNextTasks = new List<string>();
        AddThenTargets(doTaskDefinition.Then, taskName, locationHint, doExitNextTasks, nodeRegistry, internalNodeIds);

        nodeRegistry[doEntryTaskName] = new WorkflowTaskNode(
            doEntryTaskName,
            WorkflowTaskNodeKind.DoEntry,
            $"{locationHint}.do-entry",
            doTaskDefinition,
            doEntryNextTasks,
            false,
            []);

        nodeRegistry[doExitTaskName] = new WorkflowTaskNode(
            doExitTaskName,
            WorkflowTaskNodeKind.DoExit,
            $"{locationHint}.do-exit",
            doTaskDefinition,
            doExitNextTasks,
            false,
            []);

        if (doTaskDefinition.Do.Count == 0)
        {
            doEntryNextTasks.Add(doExitTaskName);
            return doTaskNextTasks;
        }

        if (doTaskDefinition.ForMaxConcurrency is not null
            && string.IsNullOrWhiteSpace(doTaskDefinition.While)
            && !string.IsNullOrWhiteSpace(doTaskDefinition.ForIn)
            && !string.IsNullOrWhiteSpace(doTaskDefinition.ForEach))
        {
            var parallelForEachTaskName = internalNodeIds.Next(taskName, "parallel_for");
            doTaskNextTasks.Clear();
            doTaskNextTasks.Add(parallelForEachTaskName);

            nodeRegistry[parallelForEachTaskName] = new WorkflowTaskNode(
                parallelForEachTaskName,
                WorkflowTaskNodeKind.ParallelForEach,
                $"{locationHint}.for",
                doTaskDefinition,
                [doExitTaskName],
                false,
                [],
                null,
                null,
                DoLoopMode.ForEach,
                null,
                doTaskDefinition.ForIn,
                doTaskDefinition.ForEach,
                doTaskDefinition.ForAt,
                doTaskDefinition.ForSize,
                doTaskDefinition.ForMaxConcurrency,
                doTaskDefinition.ForInput,
                doTaskDefinition.ForCollect,
                doTaskDefinition.ForCollectInclude?.ToArray(),
                doTaskDefinition.ForOnError,
                null,
                [doExitTaskName]);

            return doTaskNextTasks;
        }

        var isSequentialForEachLoop = string.IsNullOrWhiteSpace(doTaskDefinition.While)
            && !string.IsNullOrWhiteSpace(doTaskDefinition.ForIn)
            && !string.IsNullOrWhiteSpace(doTaskDefinition.ForEach);
        var loopGateTaskName = isSequentialForEachLoop
            ? internalNodeIds.Next(taskName, "for")
            : string.IsNullOrWhiteSpace(doTaskDefinition.While)
                ? null
                : internalNodeIds.Next(taskName, "while");
        var nestedSwitchEndTargetName = loopGateTaskName ?? doExitTaskName;
        var nestedTaskNames = doTaskDefinition.Do.Keys.ToList();
        foreach (var nestedTaskEntry in doTaskDefinition.Do)
        {
            RegisterTaskNode(
                nestedTaskEntry.Key,
                nestedTaskEntry.Value,
                BuildLocationHint(nestedTaskEntry.Key, locationHint),
                nodeRegistry,
                internalNodeIds,
                globalExtensions,
                nestedSwitchEndTargetName);
        }

        var nestedReferences = CollectNestedReferences(doTaskDefinition.Do, locationHint);
        var nestedRoots = nestedTaskNames.Where(name => !nestedReferences.Contains(name)).ToList();
        if (nestedRoots.Count == 0)
        {
            nestedRoots.Add(nestedTaskNames[0]);
        }

        foreach (var root in nestedRoots)
        {
            if (!doEntryNextTasks.Contains(root, StringComparer.Ordinal))
            {
                doEntryNextTasks.Add(root);
            }
        }

        if (!doEntryNextTasks.Contains(doExitTaskName, StringComparer.Ordinal))
        {
            doEntryNextTasks.Add(doExitTaskName);
        }

        if (string.IsNullOrWhiteSpace(doTaskDefinition.While))
        {
            if (string.IsNullOrWhiteSpace(doTaskDefinition.ForIn) || string.IsNullOrWhiteSpace(doTaskDefinition.ForEach))
            {
                AppendTargetToNestedTerminalTasks(doExitTaskName, nestedTaskNames, nodeRegistry);
                return doTaskNextTasks;
            }

            var forGateTaskName = loopGateTaskName
                ?? throw new InvalidOperationException($"Task '{taskName}' at {locationHint} could not allocate a for loop gate.");
            doTaskNextTasks.Clear();
            doTaskNextTasks.Add(forGateTaskName);

            var forExitTasks = new List<string> { doExitTaskName };
            var forContinueTasks = new List<string> { doEntryTaskName };

            nodeRegistry[forGateTaskName] = new WorkflowTaskNode(
                forGateTaskName,
                WorkflowTaskNodeKind.LoopGate,
                $"{locationHint}.for",
                doTaskDefinition,
                forContinueTasks.Concat(forExitTasks).Distinct(StringComparer.Ordinal).ToList(),
                false,
                [],
                null,
                null,
                DoLoopMode.ForEach,
                null,
                doTaskDefinition.ForIn,
                doTaskDefinition.ForEach,
                doTaskDefinition.ForAt,
                doTaskDefinition.ForSize,
                LoopBodyTaskNames: forContinueTasks,
                LoopExitTaskNames: forExitTasks.ToList());

            AppendLoopbackToNestedTerminalTasks(forGateTaskName, nestedTaskNames, nodeRegistry);
            return doTaskNextTasks;
        }

        loopGateTaskName ??= internalNodeIds.Next(taskName, "while");
        var exitTasks = new List<string> { doExitTaskName };
        var continueTasks = new List<string> { doEntryTaskName };

        nodeRegistry[loopGateTaskName] = new WorkflowTaskNode(
            loopGateTaskName,
            WorkflowTaskNodeKind.LoopGate,
            $"{locationHint}.while",
            doTaskDefinition,
            continueTasks.Concat(exitTasks).Distinct(StringComparer.Ordinal).ToList(),
            false,
            [],
            null,
            null,
            DoLoopMode.While,
            doTaskDefinition.While,
            null,
            null,
            null,
            null,
            LoopBodyTaskNames: continueTasks,
            LoopExitTaskNames: exitTasks.ToList());

        AppendLoopbackToNestedTerminalTasks(loopGateTaskName, nestedTaskNames, nodeRegistry);
        return doTaskNextTasks;
    }

    private static IReadOnlyList<string> BuildRunTaskNodes(
        RunTaskDefinition runTaskDefinition,
        string taskName,
        string locationHint,
        IDictionary<string, WorkflowTaskNode> nodeRegistry,
        InternalNodeIdGenerator internalNodeIds,
        IReadOnlyList<string> globalExtensions)
    {
        var runEntryTaskName = internalNodeIds.Next(taskName, "run_entry");
        var runExitTaskName = internalNodeIds.Next(taskName, "run_exit");
        var runTaskNextTasks = new List<string> { runEntryTaskName };
        var runEntryNextTasks = new List<string>();
        var runExitNextTasks = new List<string>();
        AddThenTargets(runTaskDefinition.Then, taskName, locationHint, runExitNextTasks, nodeRegistry, internalNodeIds);

        nodeRegistry[runEntryTaskName] = new WorkflowTaskNode(
            runEntryTaskName,
            WorkflowTaskNodeKind.RunEntry,
            $"{locationHint}.run-entry",
            runTaskDefinition,
            runEntryNextTasks,
            false,
            [],
            runTaskDefinition.Workflow.WorkflowType,
            taskName);

        nodeRegistry[runExitTaskName] = new WorkflowTaskNode(
            runExitTaskName,
            WorkflowTaskNodeKind.RunExit,
            $"{locationHint}.run-exit",
            runTaskDefinition,
            runExitNextTasks,
            false,
            [],
            runTaskDefinition.Workflow.WorkflowType,
            taskName);

        if (runTaskDefinition.Inline?.Do is not { Count: > 0 })
        {
            runEntryNextTasks.Add(runExitTaskName);
            return runTaskNextTasks;
        }

        var registryKeysBefore = nodeRegistry.Keys.ToHashSet(StringComparer.Ordinal);
        var nestedTaskNames = runTaskDefinition.Inline.TaskOrder.Count > 0
            ? runTaskDefinition.Inline.TaskOrder.ToList()
            : runTaskDefinition.Inline.Do.Keys.ToList();

        foreach (var nestedTaskEntry in runTaskDefinition.Inline.Do)
        {
            RegisterTaskNode(
                nestedTaskEntry.Key,
                nestedTaskEntry.Value,
                BuildLocationHint(nestedTaskEntry.Key, locationHint),
                nodeRegistry,
                internalNodeIds,
                globalExtensions,
                runExitTaskName);
        }

        var nestedReferences = CollectNestedReferences(runTaskDefinition.Inline.Do, locationHint);
        var nestedRoots = nestedTaskNames.Where(name => !nestedReferences.Contains(name)).ToList();
        if (nestedRoots.Count == 0)
        {
            nestedRoots.Add(nestedTaskNames[0]);
        }

        foreach (var root in nestedRoots)
        {
            if (!runEntryNextTasks.Contains(root, StringComparer.Ordinal))
            {
                runEntryNextTasks.Add(root);
            }
        }

        if (!runEntryNextTasks.Contains(runExitTaskName, StringComparer.Ordinal))
        {
            runEntryNextTasks.Add(runExitTaskName);
        }

        AppendTargetToNestedTerminalTasks(runExitTaskName, nestedTaskNames, nodeRegistry);

        foreach (var addedTaskName in nodeRegistry.Keys.Except(registryKeysBefore, StringComparer.Ordinal).ToArray())
        {
            nodeRegistry[addedTaskName] = nodeRegistry[addedTaskName] with
            {
                SourceWorkflowType = runTaskDefinition.Workflow.WorkflowType,
                ParentRunTaskName = taskName
            };
        }

        return runTaskNextTasks;
    }

    private static IReadOnlyList<string> BuildForkTaskNodes(
        ForkTaskDefinition forkTaskDefinition,
        string taskName,
        string locationHint,
        IDictionary<string, WorkflowTaskNode> nodeRegistry,
        InternalNodeIdGenerator internalNodeIds,
        IReadOnlyList<string> globalExtensions)
    {
        var forkJoinTaskName = internalNodeIds.Next(taskName, "fork_join");
        var forkNextTasks = new List<string>();
        var forkJoinNextTasks = new List<string>();
        AddThenTargets(forkTaskDefinition.Then, taskName, locationHint, forkJoinNextTasks, nodeRegistry, internalNodeIds);

        nodeRegistry[forkJoinTaskName] = new WorkflowTaskNode(
            forkJoinTaskName,
            WorkflowTaskNodeKind.ForkJoin,
            $"{locationHint}.fork.join",
            forkTaskDefinition,
            forkJoinNextTasks,
            false,
            []);

        if (forkTaskDefinition.Branches.Count == 0)
        {
            forkNextTasks.Add(forkJoinTaskName);
            return forkNextTasks;
        }

        foreach (var branch in forkTaskDefinition.Branches)
        {
            if (branch.Do.Count == 0)
            {
                continue;
            }

            var branchTaskNames = branch.Do.Keys.ToList();
            foreach (var branchTaskEntry in branch.Do)
            {
                RegisterTaskNode(branchTaskEntry.Key, branchTaskEntry.Value, BuildLocationHint(branchTaskEntry.Key, locationHint), nodeRegistry, internalNodeIds, globalExtensions, forkJoinTaskName);
            }

            var branchReferences = CollectNestedReferences(branch.Do, locationHint);
            var branchRoots = branchTaskNames.Where(name => !branchReferences.Contains(name)).ToList();
            if (branchRoots.Count == 0)
            {
                branchRoots.Add(branchTaskNames[0]);
            }

            foreach (var root in branchRoots)
            {
                if (!forkNextTasks.Contains(root, StringComparer.Ordinal))
                {
                    forkNextTasks.Add(root);
                }
            }

            AppendTargetToNestedTerminalTasks(forkJoinTaskName, branchTaskNames, nodeRegistry);
        }

        return forkNextTasks;
    }

    private static IReadOnlyList<string> BuildTryTaskNodes(
        TryTaskDefinition tryTaskDefinition,
        string taskName,
        string locationHint,
        IDictionary<string, WorkflowTaskNode> nodeRegistry,
        InternalNodeIdGenerator internalNodeIds,
        IReadOnlyList<string> globalExtensions)
    {
        var tryEntryTaskName = internalNodeIds.Next(taskName, "try_entry");
        var tryExitTaskName = internalNodeIds.Next(taskName, "try_exit");
        var catchSwitchTaskName = internalNodeIds.Next(taskName, "catch_switch");
        var tryTaskNextTasks = new List<string> { tryEntryTaskName };

        var tryEntryNextTasks = new List<string>();
        nodeRegistry[tryEntryTaskName] = new WorkflowTaskNode(
            tryEntryTaskName,
            WorkflowTaskNodeKind.DoEntry,
            $"{locationHint}.try-entry",
            tryTaskDefinition,
            tryEntryNextTasks,
            false,
            []);

        var tryExitNextTasks = new List<string>();
        AddThenTargets(tryTaskDefinition.Then, taskName, locationHint, tryExitNextTasks, nodeRegistry, internalNodeIds);
        nodeRegistry[tryExitTaskName] = new WorkflowTaskNode(
            tryExitTaskName,
            WorkflowTaskNodeKind.DoExit,
            $"{locationHint}.try-exit",
            tryTaskDefinition,
            tryExitNextTasks,
            false,
            []);

        var postCatchTargetTask = tryExitTaskName;
        if (tryTaskDefinition.Finally is { Count: > 0 })
        {
            var finallyTaskNames = tryTaskDefinition.Finally.Keys.ToList();
            foreach (var finallyTaskEntry in tryTaskDefinition.Finally)
            {
                RegisterTaskNode(finallyTaskEntry.Key, finallyTaskEntry.Value, BuildLocationHint(finallyTaskEntry.Key, locationHint), nodeRegistry, internalNodeIds, globalExtensions, tryExitTaskName);
            }

            var finallyReferences = CollectNestedReferences(tryTaskDefinition.Finally, locationHint);
            var finallyRoots = finallyTaskNames.Where(name => !finallyReferences.Contains(name)).ToList();
            if (finallyRoots.Count == 0)
            {
                finallyRoots.Add(finallyTaskNames[0]);
            }

            var finallyEntryTaskName = internalNodeIds.Next(taskName, "finally_entry");
            nodeRegistry[finallyEntryTaskName] = new WorkflowTaskNode(
                finallyEntryTaskName,
                WorkflowTaskNodeKind.Passthrough,
                $"{locationHint}.finally-entry",
                tryTaskDefinition,
                finallyRoots,
                false,
                []);

            AppendTargetToNestedTerminalTasks(tryExitTaskName, finallyTaskNames, nodeRegistry);
            postCatchTargetTask = finallyEntryTaskName;
        }

        if (tryTaskDefinition.Try.Count == 0)
        {
            tryEntryNextTasks.Add(postCatchTargetTask);
        }
        else
        {
            var protectedTaskNames = tryTaskDefinition.Try.Keys.ToList();
            foreach (var protectedTaskEntry in tryTaskDefinition.Try)
            {
                RegisterTaskNode(protectedTaskEntry.Key, protectedTaskEntry.Value, BuildLocationHint(protectedTaskEntry.Key, locationHint), nodeRegistry, internalNodeIds, globalExtensions, catchSwitchTaskName, captureErrors: true);
            }

            var protectedReferences = CollectNestedReferences(tryTaskDefinition.Try, locationHint);
            var protectedRoots = protectedTaskNames.Where(name => !protectedReferences.Contains(name)).ToList();
            if (protectedRoots.Count == 0)
            {
                protectedRoots.Add(protectedTaskNames[0]);
            }

            foreach (var root in protectedRoots)
            {
                tryEntryNextTasks.Add(root);
            }

            AppendTargetToNestedTerminalTasks(catchSwitchTaskName, protectedTaskNames, nodeRegistry);
        }

        var catchSwitchDefinition = new SwitchTaskDefinition();
        catchSwitchDefinition.Switch.Add(new KeyValuePair<string, SwitchBranchDefinition>(
            "success",
            new SwitchBranchDefinition
            {
                When = "$.__error == null",
                Then = postCatchTargetTask
            }));

        var catchIndex = 0;
        foreach (var catchDefinition in tryTaskDefinition.Catch)
        {
            if (catchDefinition.Do.Count == 0)
            {
                continue;
            }

            var catchTaskNames = catchDefinition.Do.Keys.ToList();
            foreach (var catchTaskEntry in catchDefinition.Do)
            {
                RegisterTaskNode(catchTaskEntry.Key, catchTaskEntry.Value, BuildLocationHint(catchTaskEntry.Key, locationHint), nodeRegistry, internalNodeIds, globalExtensions, postCatchTargetTask);
            }

            var catchReferences = CollectNestedReferences(catchDefinition.Do, locationHint);
            var catchRoots = catchTaskNames.Where(name => !catchReferences.Contains(name)).ToList();
            if (catchRoots.Count == 0)
            {
                catchRoots.Add(catchTaskNames[0]);
            }

            AppendTargetToNestedTerminalTasks(postCatchTargetTask, catchTaskNames, nodeRegistry);

            var catchWhen = !string.IsNullOrWhiteSpace(catchDefinition.Errors)
                ? $"$.__error.type == '{catchDefinition.Errors}'"
                : (string.IsNullOrWhiteSpace(catchDefinition.When) ? "$.__error != null" : catchDefinition.When);

            catchSwitchDefinition.Switch.Add(new KeyValuePair<string, SwitchBranchDefinition>(
                $"catch_{catchIndex++}",
                new SwitchBranchDefinition
                {
                    When = catchWhen,
                    Then = catchRoots[0]
                }));
        }

        var catchSwitchNext = catchSwitchDefinition.Switch
            .Select(entry => entry.Value.Then?.ToString())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        nodeRegistry[catchSwitchTaskName] = new WorkflowTaskNode(
            catchSwitchTaskName,
            WorkflowTaskNodeKind.Switch,
            $"{locationHint}.catch",
            catchSwitchDefinition,
            catchSwitchNext,
            false,
            []);

        return tryTaskNextTasks;
    }

    private static void AppendLoopbackToNestedTerminalTasks(
        string loopGateTaskName,
        IReadOnlyCollection<string> nestedTaskNames,
        IDictionary<string, WorkflowTaskNode> nodeRegistry)
    {
        AppendTargetToLogicalTerminalNodes(loopGateTaskName, nestedTaskNames, nodeRegistry);
    }

    private static HashSet<string> CollectNestedReferences(IDictionary<string, TaskDefinition> nestedTasks, string locationHint)
    {
        var references = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (nestedTaskName, nestedTaskDefinition) in nestedTasks)
        {
            CollectThenTargets(nestedTaskDefinition.Then, nestedTaskName, locationHint, references);

            if (nestedTaskDefinition is not SwitchTaskDefinition switchTaskDefinition)
            {
                continue;
            }

            foreach (var branch in SwitchTaskAdapter.ReadBranches(switchTaskDefinition, locationHint))
            {
                if (SwitchTaskAdapter.IsEndDirective(branch.Then))
                {
                    continue;
                }

                CollectThenTargets(branch.Then, nestedTaskName, branch.LocationHint, references);
            }
        }

        return references;
    }

    private static void CollectThenTargets(object? thenValue, string taskName, string locationHint, ISet<string> references)
    {
        var normalizer = new ThenTargetNormalizer((parentName, displayName) => $"{parentName}.__validation_{displayName}");

        foreach (var target in normalizer.Normalize(thenValue, taskName, locationHint))
        {
            if (target is NamedTaskTarget namedTaskTarget)
            {
                references.Add(namedTaskTarget.Name);
            }
        }
    }

    private static void AppendTargetToNestedTerminalTasks(
        string targetTaskName,
        IReadOnlyCollection<string> nestedTaskNames,
        IDictionary<string, WorkflowTaskNode> nodeRegistry)
    {
        AppendTargetToLogicalTerminalNodes(targetTaskName, nestedTaskNames, nodeRegistry);
    }

    private static void AppendTargetToLogicalTerminalNodes(
        string targetTaskName,
        IReadOnlyCollection<string> nestedTaskNames,
        IDictionary<string, WorkflowTaskNode> nodeRegistry)
    {
        foreach (var nestedTaskName in nestedTaskNames)
        {
            var terminalNodes = nodeRegistry.Values
                .Where(node => IsLogicalTaskNode(nestedTaskName, node.Name))
                .Where(node => node.NextTaskNames.Count == 0)
                .ToArray();

            foreach (var terminalNode in terminalNodes)
            {
                nodeRegistry[terminalNode.Name] = terminalNode with
                {
                    NextTaskNames = [targetTaskName]
                };
            }
        }
    }

    private static bool IsLogicalTaskNode(string taskName, string candidateName)
        => string.Equals(candidateName, taskName, StringComparison.Ordinal)
           || candidateName.StartsWith($"{taskName}.__internal_", StringComparison.Ordinal)
           || candidateName.StartsWith($"{taskName}.__do[", StringComparison.Ordinal)
           || candidateName.StartsWith($"{taskName}.__fork[", StringComparison.Ordinal)
           || candidateName.StartsWith($"{taskName}.__run.", StringComparison.Ordinal);

    private static void AddThenTargets(
        object? thenValue,
        string taskName,
        string locationHint,
        ICollection<string> nextTasks,
        IDictionary<string, WorkflowTaskNode> nodeRegistry,
        InternalNodeIdGenerator internalNodeIds)
    {
        var normalizer = new ThenTargetNormalizer((parentName, displayName) => internalNodeIds.Next(parentName, displayName));

        foreach (var target in normalizer.Normalize(thenValue, taskName, locationHint))
        {
            switch (target)
            {
                case NamedTaskTarget namedTarget:
                    nextTasks.Add(namedTarget.Name);
                    break;
                case InlineTaskTarget inlineTarget:
                    RegisterInlineThenTarget(inlineTarget, nextTasks, nodeRegistry, internalNodeIds);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported then target descriptor type '{target.GetType().Name}' for task '{taskName}' at {locationHint}.");
            }
        }
    }

    private static void AddSwitchTargets(
        SwitchTaskDefinition switchTaskDefinition,
        string taskName,
        string locationHint,
        ICollection<string> nextTasks,
        IDictionary<string, WorkflowTaskNode> nodeRegistry,
        InternalNodeIdGenerator internalNodeIds,
        string? switchEndTargetName)
    {
        var branches = SwitchTaskAdapter.ReadBranches(switchTaskDefinition, locationHint);

        foreach (var branch in branches)
        {
            if (SwitchTaskAdapter.IsEndDirective(branch.Then))
            {
                if (!string.IsNullOrWhiteSpace(switchEndTargetName))
                {
                    nextTasks.Add(switchEndTargetName);
                }

                continue;
            }

            AddThenTargets(branch.Then, taskName, branch.LocationHint, nextTasks, nodeRegistry, internalNodeIds);
        }
    }

    private static void RegisterInlineThenTarget(
        InlineTaskTarget inlineTarget,
        ICollection<string> nextTasks,
        IDictionary<string, WorkflowTaskNode> nodeRegistry,
        InternalNodeIdGenerator internalNodeIds)
    {
        var thenNode = inlineTarget.InlineDefinition["then"] ?? inlineTarget.InlineDefinition["Then"];

        var inlineNextTasks = new List<string>();
        AddThenTargets(thenNode, inlineTarget.InternalName, inlineTarget.LocationHint, inlineNextTasks, nodeRegistry, internalNodeIds);

        var inlineNode = new WorkflowTaskNode(
            inlineTarget.InternalName,
            WorkflowTaskNodeKind.Passthrough,
            inlineTarget.LocationHint,
            null,
            inlineNextTasks,
            false,
            []);

        nodeRegistry[inlineTarget.InternalName] = inlineNode;
        nextTasks.Add(inlineTarget.InternalName);
    }

    private static WorkflowTaskNodeKind GetNodeKind(TaskDefinition taskDefinition, string taskName, string locationHint)
    {
        return taskDefinition switch
        {
            CallTaskDefinition => WorkflowTaskNodeKind.Call,
            RunTaskDefinition => WorkflowTaskNodeKind.RunEntry,
            SwitchTaskDefinition => WorkflowTaskNodeKind.Switch,
            ListenTaskDefinition => WorkflowTaskNodeKind.Listen,
            WaitTaskDefinition => WorkflowTaskNodeKind.Wait,
            TerminateTaskDefinition => WorkflowTaskNodeKind.Terminate,
            RaiseTaskDefinition => WorkflowTaskNodeKind.Raise,
            EmitTaskDefinition => WorkflowTaskNodeKind.Emit,
            MapTaskDefinition => WorkflowTaskNodeKind.Map,
            StashTaskDefinition => WorkflowTaskNodeKind.Stash,
            DoTaskDefinition => WorkflowTaskNodeKind.DoEntry,
            TryTaskDefinition => WorkflowTaskNodeKind.DoEntry,
            ForkTaskDefinition => WorkflowTaskNodeKind.ForkSplit,
            _ => throw new InvalidOperationException($"Unsupported workflow task type '{taskDefinition.GetType().FullName}' for task '{taskName}' near {locationHint}. Supported task types are: {nameof(CallTaskDefinition)}, {nameof(RunTaskDefinition)}, {nameof(SwitchTaskDefinition)}, {nameof(ListenTaskDefinition)}, {nameof(WaitTaskDefinition)}, {nameof(TerminateTaskDefinition)}, {nameof(RaiseTaskDefinition)}, {nameof(EmitTaskDefinition)}, {nameof(MapTaskDefinition)}, {nameof(StashTaskDefinition)}, {nameof(DoTaskDefinition)}, {nameof(ForkTaskDefinition)}, {nameof(TryTaskDefinition)}.")
        };
    }

    private static string GetAuthoredTaskName(string taskName)
    {
        const string nestedTaskMarker = ".__do[";
        const string nestedWorkflowMarker = ".__run.";

        var authoredNameStart = 0;
        var doMarkerIndex = taskName.LastIndexOf(nestedTaskMarker, StringComparison.Ordinal);
        if (doMarkerIndex >= 0)
        {
            var qualifierEnd = taskName.IndexOf("].", doMarkerIndex, StringComparison.Ordinal);
            if (qualifierEnd >= 0)
            {
                authoredNameStart = qualifierEnd + 2;
            }
        }

        var runMarkerIndex = taskName.LastIndexOf(nestedWorkflowMarker, StringComparison.Ordinal);
        if (runMarkerIndex >= 0)
        {
            authoredNameStart = Math.Max(authoredNameStart, runMarkerIndex + nestedWorkflowMarker.Length);
        }

        return authoredNameStart <= 0 || authoredNameStart >= taskName.Length
            ? taskName
            : taskName[authoredNameStart..];
    }

    private static string BuildLocationHint(string taskName, string? parentLocationHint = null)
    {
        if (!string.IsNullOrWhiteSpace(parentLocationHint))
        {
            return $"{parentLocationHint}.do['{taskName}']";
        }

        return $"document.do['{taskName}']";
    }

    private static IReadOnlyList<string> CombineExtensions(IReadOnlyList<string> globalExtensions, IList<string> taskExtensions)
    {
        if (globalExtensions.Count == 0 && taskExtensions.Count == 0)
        {
            return [];
        }

        var combined = new List<string>(globalExtensions.Count + taskExtensions.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var extension in globalExtensions)
        {
            if (seen.Add(extension))
            {
                combined.Add(extension);
            }
        }

        foreach (var extension in taskExtensions)
        {
            if (seen.Add(extension))
            {
                combined.Add(extension);
            }
        }

        return combined;
    }
}
