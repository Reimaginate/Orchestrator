namespace Reimaginate.Orchestrator.DSL;

public static class WorkflowAstBinder
{
    public static WorkflowDefinition Bind(WorkflowAst ast)
    {
        ArgumentNullException.ThrowIfNull(ast);

        return new WorkflowDefinition
        {
            Metadata = ast.Metadata is null
                ? null
                : new WorkflowMetadataDefinition
                {
                    Dsl = ast.Metadata.Dsl,
                    Namespace = ast.Metadata.Namespace,
                    Name = ast.Metadata.Name,
                    Version = ast.Metadata.Version
                },
            Use = ast.Use is null
                ? null
                : new WorkflowUseDefinition
                {
                    Extensions = ast.Use.Extensions.Select(BuildExtensionDefinition).ToList()
                },
            Do = BuildTaskDictionary(ast.Tasks)
        };
    }

    private static WorkflowExtensionDefinition BuildExtensionDefinition(WorkflowExtensionAst ast)
    {
        return new WorkflowExtensionDefinition
        {
            Name = ast.Name,
            Extend = ast.Extend,
            Before = BuildTaskBlockDefinition(ast.Before),
            BeforeWhen = ast.BeforeWhen,
            After = BuildTaskBlockDefinition(ast.After),
            AfterWhen = ast.AfterWhen,
            OnError = BuildTaskBlockDefinition(ast.OnError),
            OnErrorWhen = ast.OnErrorWhen
        };
    }

    private static WorkflowTaskBlockDefinition? BuildTaskBlockDefinition(IReadOnlyList<TaskAst>? tasks)
    {
        if (tasks is null)
        {
            return null;
        }

        return new WorkflowTaskBlockDefinition
        {
            Do = BuildTaskDictionary(tasks),
            TaskOrder = tasks.Select(task => task.Name).ToList()
        };
    }

    private static Dictionary<string, TaskDefinition> BuildTaskDictionary(IReadOnlyList<TaskAst> tasks)
    {
        var mappedTasks = new Dictionary<string, TaskDefinition>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            mappedTasks[task.Name] = task switch
            {
                CallAst call => new CallTaskDefinition
                {
                    Call = call.Call,
                    With = call.With?.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
                    Output = BuildTaskOutputDefinition(call.Output),
                    Then = call.Then,
                    If = call.If,
                    Filter = call.Filter,
                    Extend = BuildExtendList(call.Extend)
                },
                RunAst run => new RunTaskDefinition
                {
                    Workflow = new RunWorkflowDefinition
                    {
                        WorkflowType = run.Workflow.WorkflowType,
                        Input = run.Workflow.Input?.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal)
                    },
                    Output = BuildTaskOutputDefinition(run.Output),
                    Then = run.Then,
                    If = run.If,
                    Filter = run.Filter,
                    Extend = BuildExtendList(run.Extend)
                },
                ListenAst listen => new ListenTaskDefinition
                {
                    ToAny = listen.ToAny.Select(target => new ListenTargetDefinition { Type = target.Type, Filter = target.Filter }).ToList(),
                    Read = listen.Read,
                    Output = BuildTaskOutputDefinition(listen.Output),
                    Then = listen.Then,
                    If = listen.If,
                    Filter = listen.Filter,
                    Extend = BuildExtendList(listen.Extend)
                },
                WaitAst wait => new WaitTaskDefinition
                {
                    For = wait.For,
                    Until = wait.Until,
                    Output = BuildTaskOutputDefinition(wait.Output),
                    Then = wait.Then,
                    If = wait.If,
                    Filter = wait.Filter,
                    Extend = BuildExtendList(wait.Extend)
                },
                TerminateAst terminate => new TerminateTaskDefinition
                {
                    Status = terminate.Status,
                    Output = terminate.Output,
                    Reason = terminate.Reason,
                    Then = terminate.Then,
                    If = terminate.If,
                    Filter = terminate.Filter,
                    Extend = BuildExtendList(terminate.Extend)
                },
                RaiseAst raise => new RaiseTaskDefinition
                {
                    ErrorType = raise.ErrorType,
                    Message = raise.Message,
                    Data = raise.Data?.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
                    Then = raise.Then,
                    If = raise.If,
                    Filter = raise.Filter,
                    Extend = BuildExtendList(raise.Extend)
                },
                EmitAst emit => new EmitTaskDefinition
                {
                    EventType = emit.EventType,
                    Source = emit.Source,
                    Subject = emit.Subject,
                    Id = emit.Id,
                    To = emit.To,
                    Data = emit.Data?.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
                    Output = BuildTaskOutputDefinition(emit.Output),
                    Then = emit.Then,
                    If = emit.If,
                    Filter = emit.Filter,
                    Extend = BuildExtendList(emit.Extend)
                },
                MapAst map => new MapTaskDefinition
                {
                    Map = BuildMapDefinition(map.Map),
                    Output = BuildTaskOutputDefinition(map.Output),
                    Then = map.Then,
                    If = map.If,
                    Filter = map.Filter,
                    Extend = BuildExtendList(map.Extend)
                },
                StashAst stash => new StashTaskDefinition
                {
                    Values = stash.Values?.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
                    Then = stash.Then,
                    If = stash.If,
                    Filter = stash.Filter,
                    Extend = BuildExtendList(stash.Extend)
                },
                SwitchAst switchAst => new SwitchTaskDefinition
                {
                    Switch = switchAst.Branches.Select(branch =>
                        new KeyValuePair<string, SwitchBranchDefinition>(branch.Name, new SwitchBranchDefinition
                        {
                            When = branch.When,
                            Then = branch.Then,
                            Output = BuildTaskOutputDefinition(branch.Output)
                        })).ToList(),
                    Then = switchAst.Then,
                    If = switchAst.If,
                    Filter = switchAst.Filter,
                    Extend = BuildExtendList(switchAst.Extend)
                },

                ForkAst forkAst => new ForkTaskDefinition
                {
                    Branches = forkAst.Branches.Select(branch => new ForkBranchDefinition
                    {
                        Name = branch.Name,
                        Do = BuildTaskDictionary(branch.Tasks)
                    }).ToList(),
                    Join = new ForkJoinDefinition { Mode = forkAst.JoinMode },
                    Output = BuildTaskOutputDefinition(forkAst.Output),
                    Then = forkAst.Then,
                    If = forkAst.If,
                    Filter = forkAst.Filter,
                    Extend = BuildExtendList(forkAst.Extend)
                },
                DoAst doAst => new DoTaskDefinition
                {
                    Do = BuildTaskDictionary(doAst.Tasks),
                    With = doAst.With?.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
                    ForIn = doAst.ForIn,
                    ForEach = doAst.ForEach,
                    ForAt = doAst.ForAt,
                    ForSize = doAst.ForSize,
                    ForMaxConcurrency = doAst.ForMaxConcurrency,
                    ForInput = doAst.ForInput?.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
                    ForCollect = doAst.ForCollect,
                    ForCollectInclude = doAst.ForCollectInclude?.ToList(),
                    ForOnError = doAst.ForOnError,
                    While = doAst.While,
                    Guard = doAst.Guard is null
                        ? null
                        : new LoopGuardPolicyDefinition
                        {
                            MaxIterations = doAst.Guard.MaxIterations,
                            MaxRepeatedPayloads = doAst.Guard.MaxRepeatedPayloads,
                            TimeoutMs = doAst.Guard.TimeoutMs,
                            OnCancel = doAst.Guard.OnCancel
                        },
                    Output = BuildTaskOutputDefinition(doAst.Output),
                    Then = doAst.Then,
                    If = doAst.If,
                    Filter = doAst.Filter,
                    Extend = BuildExtendList(doAst.Extend)
                },
                TryAst tryAst => new TryTaskDefinition
                {
                    Try = BuildTaskDictionary(tryAst.TryTasks),
                    Catch = tryAst.Catches.Select(c => new CatchDefinition
                    {
                        Errors = c.Errors,
                        When = c.When,
                        Then = c.Then,
                        Output = BuildTaskOutputDefinition(c.Output),
                        Do = BuildTaskDictionary(c.Tasks)
                    }).ToList(),
                    Finally = tryAst.FinallyTasks is null ? null : BuildTaskDictionary(tryAst.FinallyTasks),
                    Output = BuildTaskOutputDefinition(tryAst.Output),
                    Then = tryAst.Then,
                    If = tryAst.If,
                    Filter = tryAst.Filter,
                    Extend = BuildExtendList(tryAst.Extend)
                },
                _ => throw new InvalidOperationException($"Unsupported task AST node '{task.GetType().Name}'.")
            };
        }

        return mappedTasks;
    }

    private static List<string> BuildExtendList(IReadOnlyList<string>? extend)
    {
        return extend?.ToList() ?? [];
    }

    private static MapDefinition BuildMapDefinition(MapDefinitionAst ast)
    {
        return new MapDefinition
        {
            Input = ast.Input,
            Schema = ast.Schema?.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
            Options = ast.Options?.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
            Rules = ast.Rules.Select(BuildMapRuleDefinition).ToList()
        };
    }

    private static MapRuleDefinition BuildMapRuleDefinition(MapRuleAst ast)
    {
        return new MapRuleDefinition
        {
            Target = ast.Target,
            From = ast.From,
            Expression = ast.Expression,
            Constant = ast.Constant,
            HasConstant = ast.HasConstant,
            When = ast.When,
            Default = ast.Default,
            HasDefault = ast.HasDefault,
            Foreach = ast.Foreach,
            Map = ast.Map is null ? null : BuildMapDefinition(ast.Map),
            Lookup = ast.Lookup is null
                ? null
                : new MapLookupDefinition
                {
                    From = ast.Lookup.From,
                    Using = ast.Lookup.Using
                },
            Validate = ast.Validate is null
                ? null
                : new MapValidationDefinition
                {
                    Required = ast.Validate.Required,
                    Pattern = ast.Validate.Pattern
                },
            Transform = ast.Transform?.ToList() ?? []
        };
    }

    private static TaskOutputDefinition? BuildTaskOutputDefinition(TaskOutputAst? output)
    {
        if (output is null)
        {
            return null;
        }

        return new TaskOutputDefinition
        {
            As = output.As,
            Stash = output.Stash?.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal)
        };
    }
}
