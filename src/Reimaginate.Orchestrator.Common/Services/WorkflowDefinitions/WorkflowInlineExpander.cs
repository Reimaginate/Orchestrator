using Reimaginate.Mediator;
using Reimaginate.Orchestrator.Common.Requests.Internal.ResolveAndLoadWorkflowDefinition;
using Reimaginate.Orchestrator.Common.Services.WorkflowDefinitions.Compilation;
using Reimaginate.Orchestrator.DSL;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowDefinitions;

internal sealed class WorkflowInlineExpander(IMediator mediator)
{
    public async Task<WorkflowDefinition> ExpandAsync(string rootWorkflowType, WorkflowDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var workflowPath = string.IsNullOrWhiteSpace(rootWorkflowType)
            ? Array.Empty<string>()
            : [rootWorkflowType];

        return await ExpandWorkflowAsync(definition, workflowPath, cancellationToken);
    }

    private async Task<WorkflowDefinition> ExpandWorkflowAsync(
        WorkflowDefinition definition,
        IReadOnlyList<string> workflowPath,
        CancellationToken cancellationToken)
    {
        var expandedTasks = new Dictionary<string, TaskDefinition>(StringComparer.Ordinal);
        var mergedExtensions = definition.Use?.Extensions.Select(CloneExtension).ToList() ?? [];

        foreach (var task in definition.Do)
        {
            var expandedTask = await ExpandTaskAsync(task.Key, task.Value, workflowPath, cancellationToken);
            expandedTasks[task.Key] = expandedTask.Task;

            foreach (var extension in expandedTask.Extensions)
            {
                mergedExtensions.Add(extension);
            }
        }

        return new WorkflowDefinition
        {
            Metadata = CloneMetadata(definition.Metadata),
            Use = mergedExtensions.Count == 0
                ? null
                : new WorkflowUseDefinition
                {
                    Extensions = mergedExtensions
                },
            Do = expandedTasks
        };
    }

    private async Task<ExpandedTask> ExpandTaskAsync(
        string taskName,
        TaskDefinition definition,
        IReadOnlyList<string> workflowPath,
        CancellationToken cancellationToken)
    {
        return definition switch
        {
            RunTaskDefinition runTask => await ExpandRunTaskAsync(taskName, runTask, workflowPath, cancellationToken),
            DoTaskDefinition doTask => new ExpandedTask(await ExpandDoTaskAsync(doTask, workflowPath, cancellationToken), []),
            ForkTaskDefinition forkTask => new ExpandedTask(await ExpandForkTaskAsync(forkTask, workflowPath, cancellationToken), []),
            TryTaskDefinition tryTask => new ExpandedTask(await ExpandTryTaskAsync(tryTask, workflowPath, cancellationToken), []),
            _ => new ExpandedTask(CloneTask(definition), [])
        };
    }

    private async Task<ExpandedTask> ExpandRunTaskAsync(
        string taskName,
        RunTaskDefinition runTask,
        IReadOnlyList<string> workflowPath,
        CancellationToken cancellationToken)
    {
        var workflowType = runTask.Workflow.WorkflowType;
        if (string.IsNullOrWhiteSpace(workflowType))
        {
            return new ExpandedTask(CloneRunTask(runTask, null), []);
        }

        if (workflowPath.Contains(workflowType, StringComparer.OrdinalIgnoreCase))
        {
            var recursionPath = string.Join(" -> ", workflowPath.Concat([workflowType]));
            throw new InvalidOperationException($"Recursive inline workflow reference detected: {recursionPath}");
        }

        var (definitionResponse, definitionError) = await mediator.TrySend(new ResolveAndLoadWorkflowDefinitionRequest
        {
            WorkflowType = workflowType
        }, cancellationToken);

        if (definitionResponse is null || definitionError is not null || !definitionResponse.Success)
        {
            var failureReason = definitionResponse?.FailureReason ?? definitionError?.Message ?? "Workflow definition could not be resolved.";
            throw new InvalidOperationException($"Task '{taskName}' could not inline workflow '{workflowType}': {failureReason}");
        }

        var validation = new WorkflowSemanticValidator().Validate(definitionResponse.Ast);
        if (!validation.IsSuccessful)
        {
            var errors = string.Join(Environment.NewLine, validation.Diagnostics.Select(diagnostic => diagnostic.Message).Distinct(StringComparer.Ordinal));
            throw new InvalidOperationException($"Task '{taskName}' could not inline workflow '{workflowType}': {errors}");
        }

        var expandedChild = await ExpandWorkflowAsync(definitionResponse.Definition, workflowPath.Concat([workflowType]).ToArray(), cancellationToken);
        var scopedChild = ScopeChildWorkflow(taskName, expandedChild);

        return new ExpandedTask(
            CloneRunTask(runTask, new WorkflowTaskBlockDefinition
            {
                Do = scopedChild.Do,
                TaskOrder = scopedChild.Do.Keys.ToList()
            }),
            scopedChild.Use?.Extensions?.ToArray() ?? []);
    }

    private async Task<DoTaskDefinition> ExpandDoTaskAsync(
        DoTaskDefinition doTask,
        IReadOnlyList<string> workflowPath,
        CancellationToken cancellationToken)
    {
        var expandedTasks = new Dictionary<string, TaskDefinition>(StringComparer.Ordinal);
        foreach (var task in doTask.Do)
        {
            var expandedTask = await ExpandTaskAsync(task.Key, task.Value, workflowPath, cancellationToken);
            expandedTasks[task.Key] = expandedTask.Task;
        }

        return new DoTaskDefinition
        {
            Do = expandedTasks,
            With = CloneDictionary(doTask.With),
            ForIn = doTask.ForIn,
            ForEach = doTask.ForEach,
            ForAt = doTask.ForAt,
            ForSize = doTask.ForSize,
            ForMaxConcurrency = doTask.ForMaxConcurrency,
            ForInput = CloneDictionary(doTask.ForInput),
            ForCollect = doTask.ForCollect,
            ForCollectInclude = doTask.ForCollectInclude?.ToList(),
            ForOnError = doTask.ForOnError,
            While = doTask.While,
            Guard = CloneGuard(doTask.Guard),
            Output = CloneOutput(doTask.Output),
            Then = CloneUntyped(doTask.Then),
            If = doTask.If,
            Filter = doTask.Filter,
            Extend = doTask.Extend.ToList()
        };
    }

    private async Task<ForkTaskDefinition> ExpandForkTaskAsync(
        ForkTaskDefinition forkTask,
        IReadOnlyList<string> workflowPath,
        CancellationToken cancellationToken)
    {
        var expandedBranches = new List<ForkBranchDefinition>();
        foreach (var branch in forkTask.Branches)
        {
            var expandedTasks = new Dictionary<string, TaskDefinition>(StringComparer.Ordinal);
            foreach (var task in branch.Do)
            {
                var expandedTask = await ExpandTaskAsync(task.Key, task.Value, workflowPath, cancellationToken);
                expandedTasks[task.Key] = expandedTask.Task;
            }

            expandedBranches.Add(new ForkBranchDefinition
            {
                Name = branch.Name,
                Do = expandedTasks
            });
        }

        return new ForkTaskDefinition
        {
            Branches = expandedBranches,
            Join = forkTask.Join is null ? null : new ForkJoinDefinition { Mode = forkTask.Join.Mode },
            Output = CloneOutput(forkTask.Output),
            Then = CloneUntyped(forkTask.Then),
            If = forkTask.If,
            Filter = forkTask.Filter,
            Extend = forkTask.Extend.ToList()
        };
    }

    private async Task<TryTaskDefinition> ExpandTryTaskAsync(
        TryTaskDefinition tryTask,
        IReadOnlyList<string> workflowPath,
        CancellationToken cancellationToken)
    {
        var expandedTryTasks = new Dictionary<string, TaskDefinition>(StringComparer.Ordinal);
        foreach (var task in tryTask.Try)
        {
            var expandedTask = await ExpandTaskAsync(task.Key, task.Value, workflowPath, cancellationToken);
            expandedTryTasks[task.Key] = expandedTask.Task;
        }

        var expandedCatches = new List<CatchDefinition>();
        foreach (var catchDefinition in tryTask.Catch)
        {
            var expandedCatchTasks = new Dictionary<string, TaskDefinition>(StringComparer.Ordinal);
            foreach (var task in catchDefinition.Do)
            {
                var expandedTask = await ExpandTaskAsync(task.Key, task.Value, workflowPath, cancellationToken);
                expandedCatchTasks[task.Key] = expandedTask.Task;
            }

            expandedCatches.Add(new CatchDefinition
            {
                Errors = catchDefinition.Errors,
                When = catchDefinition.When,
                Do = expandedCatchTasks,
                Output = CloneOutput(catchDefinition.Output),
                Then = CloneUntyped(catchDefinition.Then)
            });
        }

        Dictionary<string, TaskDefinition>? expandedFinally = null;
        if (tryTask.Finally is not null)
        {
            expandedFinally = new Dictionary<string, TaskDefinition>(StringComparer.Ordinal);
            foreach (var task in tryTask.Finally)
            {
                var expandedTask = await ExpandTaskAsync(task.Key, task.Value, workflowPath, cancellationToken);
                expandedFinally[task.Key] = expandedTask.Task;
            }
        }

        return new TryTaskDefinition
        {
            Try = expandedTryTasks,
            Catch = expandedCatches,
            Finally = expandedFinally,
            Output = CloneOutput(tryTask.Output),
            Then = CloneUntyped(tryTask.Then),
            If = tryTask.If,
            Filter = tryTask.Filter,
            Extend = tryTask.Extend.ToList()
        };
    }

    private static WorkflowDefinition ScopeChildWorkflow(string parentTaskName, WorkflowDefinition childDefinition)
    {
        var taskNames = CollectTaskNames(childDefinition.Do).ToArray();
        var taskRenameMap = taskNames.ToDictionary(name => name, name => $"{parentTaskName}.__run.{name}", StringComparer.Ordinal);
        var extensionRenameMap = (childDefinition.Use?.Extensions ?? [])
            .ToDictionary(extension => extension.Name, extension => $"{parentTaskName}.__run.ext.{extension.Name}", StringComparer.Ordinal);

        var scopedTasks = new Dictionary<string, TaskDefinition>(StringComparer.Ordinal);
        foreach (var task in childDefinition.Do)
        {
            scopedTasks[taskRenameMap[task.Key]] = RenameTask(task.Value, taskRenameMap, extensionRenameMap);
        }

        var scopedExtensions = (childDefinition.Use?.Extensions ?? [])
            .Select(extension => RenameExtension(extension, extensionRenameMap))
            .ToList();

        foreach (var extension in childDefinition.Use?.Extensions?.Where(extension => string.Equals(extension.Extend, "all", StringComparison.OrdinalIgnoreCase)) ?? [])
        {
            ApplyExtensionToAllTasks(scopedTasks, extensionRenameMap[extension.Name]);
        }

        return new WorkflowDefinition
        {
            Metadata = CloneMetadata(childDefinition.Metadata),
            Use = scopedExtensions.Count == 0
                ? null
                : new WorkflowUseDefinition
                {
                    Extensions = scopedExtensions
                },
            Do = scopedTasks
        };
    }

    private static IEnumerable<string> CollectTaskNames(IDictionary<string, TaskDefinition> tasks)
    {
        foreach (var task in tasks)
        {
            yield return task.Key;

            switch (task.Value)
            {
                case DoTaskDefinition doTask:
                    foreach (var nested in CollectTaskNames(doTask.Do))
                    {
                        yield return nested;
                    }

                    break;
                case ForkTaskDefinition forkTask:
                    foreach (var nested in forkTask.Branches.SelectMany(branch => CollectTaskNames(branch.Do)))
                    {
                        yield return nested;
                    }

                    break;
                case TryTaskDefinition tryTask:
                    foreach (var nested in CollectTaskNames(tryTask.Try))
                    {
                        yield return nested;
                    }

                    foreach (var nested in tryTask.Catch.SelectMany(c => CollectTaskNames(c.Do)))
                    {
                        yield return nested;
                    }

                    if (tryTask.Finally is not null)
                    {
                        foreach (var nested in CollectTaskNames(tryTask.Finally))
                        {
                            yield return nested;
                        }
                    }

                    break;
                case RunTaskDefinition runTask when runTask.Inline is not null:
                    foreach (var nested in CollectTaskNames(runTask.Inline.Do))
                    {
                        yield return nested;
                    }

                    break;
            }
        }
    }

    private static void ApplyExtensionToAllTasks(IDictionary<string, TaskDefinition> tasks, string extensionName)
    {
        foreach (var task in tasks.Values)
        {
            if (WorkflowExtensionWrappingSupport.SupportsJsonHookWrapping(task)
                && !task.Extend.Contains(extensionName, StringComparer.Ordinal))
            {
                task.Extend.Add(extensionName);
            }

            switch (task)
            {
                case DoTaskDefinition doTask:
                    ApplyExtensionToAllTasks(doTask.Do, extensionName);
                    break;
                case ForkTaskDefinition forkTask:
                    foreach (var branch in forkTask.Branches)
                    {
                        ApplyExtensionToAllTasks(branch.Do, extensionName);
                    }

                    break;
                case TryTaskDefinition tryTask:
                    ApplyExtensionToAllTasks(tryTask.Try, extensionName);
                    foreach (var catchDefinition in tryTask.Catch)
                    {
                        ApplyExtensionToAllTasks(catchDefinition.Do, extensionName);
                    }

                    if (tryTask.Finally is not null)
                    {
                        ApplyExtensionToAllTasks(tryTask.Finally, extensionName);
                    }

                    break;
                case RunTaskDefinition runTask when runTask.Inline is not null:
                    ApplyExtensionToAllTasks(runTask.Inline.Do, extensionName);
                    break;
            }
        }
    }

    private static WorkflowExtensionDefinition CloneExtension(WorkflowExtensionDefinition extension)
    {
        return new WorkflowExtensionDefinition
        {
            Name = extension.Name,
            Extend = extension.Extend,
            Before = CloneTaskBlock(extension.Before, null, null),
            BeforeWhen = extension.BeforeWhen,
            After = CloneTaskBlock(extension.After, null, null),
            AfterWhen = extension.AfterWhen,
            OnError = CloneTaskBlock(extension.OnError, null, null),
            OnErrorWhen = extension.OnErrorWhen
        };
    }

    private static WorkflowExtensionDefinition RenameExtension(WorkflowExtensionDefinition extension, IReadOnlyDictionary<string, string> extensionRenameMap)
    {
        return new WorkflowExtensionDefinition
        {
            Name = extensionRenameMap[extension.Name],
            Extend = null,
            Before = CloneTaskBlock(extension.Before, null, extensionRenameMap),
            BeforeWhen = extension.BeforeWhen,
            After = CloneTaskBlock(extension.After, null, extensionRenameMap),
            AfterWhen = extension.AfterWhen,
            OnError = CloneTaskBlock(extension.OnError, null, extensionRenameMap),
            OnErrorWhen = extension.OnErrorWhen
        };
    }

    private static WorkflowTaskBlockDefinition? CloneTaskBlock(
        WorkflowTaskBlockDefinition? taskBlock,
        IReadOnlyDictionary<string, string>? taskRenameMap,
        IReadOnlyDictionary<string, string>? extensionRenameMap)
    {
        if (taskBlock is null)
        {
            return null;
        }

        var clonedTasks = new Dictionary<string, TaskDefinition>(StringComparer.Ordinal);
        foreach (var task in taskBlock.Do)
        {
            var taskName = taskRenameMap is not null && taskRenameMap.TryGetValue(task.Key, out var renamedTaskName)
                ? renamedTaskName
                : task.Key;
            clonedTasks[taskName] = RenameTask(task.Value, taskRenameMap, extensionRenameMap);
        }

        var taskOrder = taskBlock.TaskOrder.Select(taskName => taskRenameMap is not null && taskRenameMap.TryGetValue(taskName, out var renamedTaskName)
            ? renamedTaskName
            : taskName).ToList();

        return new WorkflowTaskBlockDefinition
        {
            Do = clonedTasks,
            TaskOrder = taskOrder
        };
    }

    private static TaskDefinition RenameTask(
        TaskDefinition definition,
        IReadOnlyDictionary<string, string>? taskRenameMap,
        IReadOnlyDictionary<string, string>? extensionRenameMap)
    {
        return definition switch
        {
            CallTaskDefinition callTask => new CallTaskDefinition
            {
                Call = callTask.Call,
                With = CloneDictionary(callTask.With),
                Output = CloneOutput(callTask.Output),
                Then = RenameThen(callTask.Then, taskRenameMap),
                If = callTask.If,
                Filter = callTask.Filter,
                Extend = RenameExtensions(callTask.Extend, extensionRenameMap)
            },
            RunTaskDefinition runTask => CloneRunTask(runTask, CloneTaskBlock(runTask.Inline, taskRenameMap, extensionRenameMap), taskRenameMap, extensionRenameMap),
            ListenTaskDefinition listenTask => new ListenTaskDefinition
            {
                ToAny = listenTask.ToAny.Select(target => new ListenTargetDefinition
                {
                    Type = target.Type,
                    Filter = target.Filter
                }).ToList(),
                Read = listenTask.Read,
                Output = CloneOutput(listenTask.Output),
                Then = RenameThen(listenTask.Then, taskRenameMap),
                If = listenTask.If,
                Filter = listenTask.Filter,
                Extend = RenameExtensions(listenTask.Extend, extensionRenameMap)
            },
            WaitTaskDefinition waitTask => new WaitTaskDefinition
            {
                For = waitTask.For,
                Until = waitTask.Until,
                Output = CloneOutput(waitTask.Output),
                Then = RenameThen(waitTask.Then, taskRenameMap),
                If = waitTask.If,
                Filter = waitTask.Filter,
                Extend = RenameExtensions(waitTask.Extend, extensionRenameMap)
            },
            TerminateTaskDefinition terminateTask => new TerminateTaskDefinition
            {
                Status = terminateTask.Status,
                Output = CloneUntyped(terminateTask.Output),
                Reason = terminateTask.Reason,
                Then = RenameThen(terminateTask.Then, taskRenameMap),
                If = terminateTask.If,
                Filter = terminateTask.Filter,
                Extend = RenameExtensions(terminateTask.Extend, extensionRenameMap)
            },
            RaiseTaskDefinition raiseTask => new RaiseTaskDefinition
            {
                ErrorType = raiseTask.ErrorType,
                Message = raiseTask.Message,
                Data = CloneDictionary(raiseTask.Data),
                Then = RenameThen(raiseTask.Then, taskRenameMap),
                If = raiseTask.If,
                Filter = raiseTask.Filter,
                Extend = RenameExtensions(raiseTask.Extend, extensionRenameMap)
            },
            EmitTaskDefinition emitTask => new EmitTaskDefinition
            {
                EventType = emitTask.EventType,
                Source = emitTask.Source,
                Subject = emitTask.Subject,
                Id = emitTask.Id,
                To = emitTask.To,
                Data = CloneDictionary(emitTask.Data),
                Output = CloneOutput(emitTask.Output),
                Then = RenameThen(emitTask.Then, taskRenameMap),
                If = emitTask.If,
                Filter = emitTask.Filter,
                Extend = RenameExtensions(emitTask.Extend, extensionRenameMap)
            },
            MapTaskDefinition mapTask => new MapTaskDefinition
            {
                Map = CloneMap(mapTask.Map),
                Output = CloneOutput(mapTask.Output),
                Then = RenameThen(mapTask.Then, taskRenameMap),
                If = mapTask.If,
                Filter = mapTask.Filter,
                Extend = RenameExtensions(mapTask.Extend, extensionRenameMap)
            },
            StashTaskDefinition stashTask => new StashTaskDefinition
            {
                Values = CloneDictionary(stashTask.Values),
                Then = RenameThen(stashTask.Then, taskRenameMap),
                If = stashTask.If,
                Filter = stashTask.Filter,
                Extend = RenameExtensions(stashTask.Extend, extensionRenameMap)
            },
            SwitchTaskDefinition switchTask => new SwitchTaskDefinition
            {
                Switch = switchTask.Switch.Select(branch => new KeyValuePair<string, SwitchBranchDefinition>(branch.Key, new SwitchBranchDefinition
                {
                    When = branch.Value.When,
                    Output = CloneOutput(branch.Value.Output),
                    Then = RenameThen(branch.Value.Then, taskRenameMap)
                })).ToList(),
                Then = RenameThen(switchTask.Then, taskRenameMap),
                If = switchTask.If,
                Filter = switchTask.Filter,
                Extend = RenameExtensions(switchTask.Extend, extensionRenameMap)
            },
            DoTaskDefinition doTask => new DoTaskDefinition
            {
                Do = doTask.Do.ToDictionary(
                    task => taskRenameMap is not null && taskRenameMap.TryGetValue(task.Key, out var renamedTaskName) ? renamedTaskName : task.Key,
                    task => RenameTask(task.Value, taskRenameMap, extensionRenameMap),
                    StringComparer.Ordinal),
                With = CloneDictionary(doTask.With),
                ForIn = doTask.ForIn,
                ForEach = doTask.ForEach,
                ForAt = doTask.ForAt,
                ForSize = doTask.ForSize,
                ForMaxConcurrency = doTask.ForMaxConcurrency,
                ForInput = CloneDictionary(doTask.ForInput),
                ForCollect = doTask.ForCollect,
                ForCollectInclude = doTask.ForCollectInclude?.ToList(),
                ForOnError = doTask.ForOnError,
                While = doTask.While,
                Guard = CloneGuard(doTask.Guard),
                Output = CloneOutput(doTask.Output),
                Then = RenameThen(doTask.Then, taskRenameMap),
                If = doTask.If,
                Filter = doTask.Filter,
                Extend = RenameExtensions(doTask.Extend, extensionRenameMap)
            },
            ForkTaskDefinition forkTask => new ForkTaskDefinition
            {
                Branches = forkTask.Branches.Select(branch => new ForkBranchDefinition
                {
                    Name = branch.Name,
                    Do = branch.Do.ToDictionary(
                        task => taskRenameMap is not null && taskRenameMap.TryGetValue(task.Key, out var renamedTaskName) ? renamedTaskName : task.Key,
                        task => RenameTask(task.Value, taskRenameMap, extensionRenameMap),
                        StringComparer.Ordinal)
                }).ToList(),
                Join = forkTask.Join is null ? null : new ForkJoinDefinition { Mode = forkTask.Join.Mode },
                Output = CloneOutput(forkTask.Output),
                Then = RenameThen(forkTask.Then, taskRenameMap),
                If = forkTask.If,
                Filter = forkTask.Filter,
                Extend = RenameExtensions(forkTask.Extend, extensionRenameMap)
            },
            TryTaskDefinition tryTask => new TryTaskDefinition
            {
                Try = tryTask.Try.ToDictionary(
                    task => taskRenameMap is not null && taskRenameMap.TryGetValue(task.Key, out var renamedTaskName) ? renamedTaskName : task.Key,
                    task => RenameTask(task.Value, taskRenameMap, extensionRenameMap),
                    StringComparer.Ordinal),
                Catch = tryTask.Catch.Select(catchDefinition => new CatchDefinition
                {
                    Errors = catchDefinition.Errors,
                    When = catchDefinition.When,
                    Do = catchDefinition.Do.ToDictionary(
                        task => taskRenameMap is not null && taskRenameMap.TryGetValue(task.Key, out var renamedTaskName) ? renamedTaskName : task.Key,
                        task => RenameTask(task.Value, taskRenameMap, extensionRenameMap),
                        StringComparer.Ordinal),
                    Output = CloneOutput(catchDefinition.Output),
                    Then = RenameThen(catchDefinition.Then, taskRenameMap)
                }).ToList(),
                Finally = tryTask.Finally?.ToDictionary(
                    task => taskRenameMap is not null && taskRenameMap.TryGetValue(task.Key, out var renamedTaskName) ? renamedTaskName : task.Key,
                    task => RenameTask(task.Value, taskRenameMap, extensionRenameMap),
                    StringComparer.Ordinal),
                Output = CloneOutput(tryTask.Output),
                Then = RenameThen(tryTask.Then, taskRenameMap),
                If = tryTask.If,
                Filter = tryTask.Filter,
                Extend = RenameExtensions(tryTask.Extend, extensionRenameMap)
            },
            _ => CloneTask(definition)
        };
    }

    private static RunTaskDefinition CloneRunTask(
        RunTaskDefinition runTask,
        WorkflowTaskBlockDefinition? inline,
        IReadOnlyDictionary<string, string>? taskRenameMap = null,
        IReadOnlyDictionary<string, string>? extensionRenameMap = null)
    {
        return new RunTaskDefinition
        {
            Workflow = new RunWorkflowDefinition
            {
                WorkflowType = runTask.Workflow.WorkflowType,
                Input = CloneDictionary(runTask.Workflow.Input)
            },
            Output = CloneOutput(runTask.Output),
            Inline = inline,
            Then = RenameThen(runTask.Then, taskRenameMap),
            If = runTask.If,
            Filter = runTask.Filter,
            Extend = RenameExtensions(runTask.Extend, extensionRenameMap)
        };
    }

    private static TaskDefinition CloneTask(TaskDefinition definition)
        => RenameTask(definition, null, null);

    private static WorkflowMetadataDefinition? CloneMetadata(WorkflowMetadataDefinition? metadata)
    {
        return metadata is null
            ? null
            : new WorkflowMetadataDefinition
            {
                Dsl = metadata.Dsl,
                Namespace = metadata.Namespace,
                Name = metadata.Name,
                Version = metadata.Version
            };
    }

    private static LoopGuardPolicyDefinition? CloneGuard(LoopGuardPolicyDefinition? guard)
    {
        return guard is null
            ? null
            : new LoopGuardPolicyDefinition
            {
                MaxIterations = guard.MaxIterations,
                MaxRepeatedPayloads = guard.MaxRepeatedPayloads,
                TimeoutMs = guard.TimeoutMs,
                OnCancel = guard.OnCancel
            };
    }

    private static TaskOutputDefinition? CloneOutput(TaskOutputDefinition? output)
    {
        return output is null
            ? null
            : new TaskOutputDefinition
            {
                As = CloneUntyped(output.As),
                Stash = CloneDictionary(output.Stash)
            };
    }

    private static MapDefinition CloneMap(MapDefinition mapDefinition)
    {
        return new MapDefinition
        {
            Input = mapDefinition.Input,
            Schema = CloneDictionary(mapDefinition.Schema),
            Options = CloneDictionary(mapDefinition.Options),
            Rules = mapDefinition.Rules.Select(rule => new MapRuleDefinition
            {
                Target = rule.Target,
                From = rule.From,
                Expression = rule.Expression,
                Constant = CloneUntyped(rule.Constant),
                HasConstant = rule.HasConstant,
                When = rule.When,
                Transform = rule.Transform.ToList(),
                Default = CloneUntyped(rule.Default),
                HasDefault = rule.HasDefault,
                Foreach = rule.Foreach,
                Map = rule.Map is null ? null : CloneMap(rule.Map),
                Lookup = rule.Lookup is null ? null : new MapLookupDefinition
                {
                    From = rule.Lookup.From,
                    Using = rule.Lookup.Using
                },
                Validate = rule.Validate is null ? null : new MapValidationDefinition
                {
                    Required = rule.Validate.Required,
                    Pattern = rule.Validate.Pattern
                }
            }).ToList()
        };
    }

    private static IDictionary<string, object>? CloneDictionary(IDictionary<string, object>? values)
    {
        return values is null
            ? null
            : values.ToDictionary(entry => entry.Key, entry => CloneUntyped(entry.Value)!, StringComparer.Ordinal);
    }

    private static List<string> RenameExtensions(IList<string> extensions, IReadOnlyDictionary<string, string>? extensionRenameMap)
    {
        return extensions.Select(extension => extensionRenameMap is not null && extensionRenameMap.TryGetValue(extension, out var renamedExtension)
            ? renamedExtension
            : extension).ToList();
    }

    private static object? RenameThen(object? thenValue, IReadOnlyDictionary<string, string>? taskRenameMap)
    {
        if (taskRenameMap is null)
        {
            return CloneUntyped(thenValue);
        }

        return thenValue switch
        {
            null => null,
            string taskName when taskRenameMap.TryGetValue(taskName, out var renamedTaskName) => renamedTaskName,
            string taskName => taskName,
            List<object?> list => list.Select(item => RenameThen(item, taskRenameMap)).ToList(),
            Dictionary<string, object?> mapping => RenameInlineThenTaskBlock(mapping, taskRenameMap),
            _ => CloneUntyped(thenValue)
        };
    }

    private static object RenameInlineThenTaskBlock(
        IReadOnlyDictionary<string, object?> mapping,
        IReadOnlyDictionary<string, string> taskRenameMap)
    {
        if (mapping.Count != 1)
        {
            return CloneUntyped(mapping)!;
        }

        var entry = mapping.Single();
        if (entry.Value is not Dictionary<string, object?> taskDefinition)
        {
            return CloneUntyped(mapping)!;
        }

        var renamedTaskDefinition = new Dictionary<string, object?>(taskDefinition, StringComparer.Ordinal);
        if (renamedTaskDefinition.TryGetValue("then", out var thenValue))
        {
            renamedTaskDefinition["then"] = RenameThen(thenValue, taskRenameMap);
        }
        else if (renamedTaskDefinition.TryGetValue("Then", out var pascalThenValue))
        {
            renamedTaskDefinition["Then"] = RenameThen(pascalThenValue, taskRenameMap);
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [entry.Key] = renamedTaskDefinition
        };
    }

    private static object? CloneUntyped(object? value)
    {
        return value switch
        {
            null => null,
            Dictionary<string, object?> dictionary => dictionary.ToDictionary(entry => entry.Key, entry => CloneUntyped(entry.Value), StringComparer.Ordinal),
            IDictionary<string, object> dictionary => dictionary.ToDictionary(entry => entry.Key, entry => CloneUntyped(entry.Value), StringComparer.Ordinal),
            List<object?> list => list.Select(CloneUntyped).ToList(),
            IReadOnlyList<object?> list => list.Select(CloneUntyped).ToList(),
            string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
            _ => value
        };
    }

    private sealed record ExpandedTask(TaskDefinition Task, IReadOnlyCollection<WorkflowExtensionDefinition> Extensions);
}
