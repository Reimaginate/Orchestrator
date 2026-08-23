using System.Text.Json.Nodes;
using System.Xml;
using Reimaginate.Orchestrator.Common.Helpers;
using Reimaginate.Orchestrator.Common.Constants;
using Reimaginate.Orchestrator.DSL;

namespace Reimaginate.Orchestrator.Common.Requests.Internal.BuildAgentWorkflow;

internal sealed record WorkflowDefinitionValidationIssue(string Message);

internal sealed record WorkflowDefinitionValidationResult(IReadOnlyList<WorkflowDefinitionValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

internal sealed class WorkflowDefinitionValidator
{
    public WorkflowDefinitionValidationResult Validate(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var issues = new List<WorkflowDefinitionValidationIssue>();

        if (definition.Do is null || definition.Do.Count == 0)
        {
            issues.Add(new WorkflowDefinitionValidationIssue("Workflow definition does not contain any tasks."));
            return new WorkflowDefinitionValidationResult(issues);
        }

        var definedTaskNames = new HashSet<string>(definition.Do.Keys, StringComparer.Ordinal);
        var referencedTaskNames = new HashSet<string>(StringComparer.Ordinal);
        var callableTasks = 0;

        foreach (var taskEntry in definition.Do)
        {
            var taskName = taskEntry.Key;
            var locationHint = BuildLocationHint(taskName);
            var taskDefinition = taskEntry.Value;

            switch (taskDefinition)
            {
                case CallTaskDefinition callTask:
                    callableTasks++;
                    if (string.IsNullOrWhiteSpace(callTask.Call))
                    {
                        issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {locationHint} is missing a callable target in 'call'."));
                    }
                    break;
                case RunTaskDefinition runTask:
                    callableTasks++;
                    ValidateRunTask(runTask, taskName, locationHint, issues, ref callableTasks);
                    break;
                case SwitchTaskDefinition switchTask:
                    ValidateSwitchBranches(switchTask, taskName, locationHint, issues, referencedTaskNames);
                    break;
                case ListenTaskDefinition listenTask:
                    ValidateListenTask(listenTask, taskName, locationHint, issues);
                    break;
                case DoTaskDefinition doTask:
                    ValidateDoTask(doTask, taskName, locationHint, issues, ref callableTasks);
                    break;
                case ForkTaskDefinition forkTask:
                    ValidateForkTask(forkTask, taskName, locationHint, issues, ref callableTasks);
                    break;
                case WaitTaskDefinition waitTask:
                    ValidateWaitTask(waitTask, taskName, locationHint, issues);
                    break;
                case TerminateTaskDefinition terminateTask:
                    ValidateTerminateTask(terminateTask, taskName, locationHint, issues);
                    callableTasks++;
                    break;
                case RaiseTaskDefinition raiseTask:
                    ValidateRaiseTask(raiseTask, taskName, locationHint, issues);
                    break;
                case EmitTaskDefinition emitTask:
                    ValidateEmitTask(emitTask, taskName, locationHint, issues);
                    break;
                case MapTaskDefinition mapTask:
                    ValidateMapTask(mapTask, taskName, locationHint, issues);
                    callableTasks++;
                    break;
                case StashTaskDefinition stashTask:
                    ValidateStashTask(stashTask, taskName, locationHint, issues);
                    callableTasks++;
                    break;
                default:
                    issues.Add(new WorkflowDefinitionValidationIssue($"Unsupported workflow task type '{taskDefinition.GetType().FullName}' for task '{taskName}' near {locationHint}. Supported task types are: {nameof(CallTaskDefinition)}, {nameof(RunTaskDefinition)}, {nameof(SwitchTaskDefinition)}, {nameof(ListenTaskDefinition)}, {nameof(WaitTaskDefinition)}, {nameof(TerminateTaskDefinition)}, {nameof(RaiseTaskDefinition)}, {nameof(EmitTaskDefinition)}, {nameof(MapTaskDefinition)}, {nameof(StashTaskDefinition)}, {nameof(DoTaskDefinition)}, {nameof(ForkTaskDefinition)}, {nameof(TryTaskDefinition)}."));
                    break;
            }

            CollectReferencedTaskNames(taskDefinition.Then, taskName, locationHint, referencedTaskNames, issues);
        }

        foreach (var referencedTaskName in referencedTaskNames)
        {
            if (!definedTaskNames.Contains(referencedTaskName))
            {
                issues.Add(new WorkflowDefinitionValidationIssue($"Task '{referencedTaskName}' is referenced but not defined. Verify the task name near {BuildLocationHint(referencedTaskName)}."));
            }
        }

        if (callableTasks == 0)
        {
            issues.Add(new WorkflowDefinitionValidationIssue("Workflow definition does not contain any callable tasks."));
        }

        return new WorkflowDefinitionValidationResult(issues);
    }

    private static void ValidateRunTask(
        RunTaskDefinition runTaskDefinition,
        string taskName,
        string locationHint,
        ICollection<WorkflowDefinitionValidationIssue> issues,
        ref int callableTasks)
    {
        if (string.IsNullOrWhiteSpace(runTaskDefinition.Workflow.WorkflowType))
        {
            issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {locationHint}.run.workflow.workflowType must define a non-empty workflow type."));
        }

        ValidateConditionExpression(runTaskDefinition.If, taskName, "if", $"{locationHint}.if", issues);
        ValidateConditionExpression(runTaskDefinition.Filter, taskName, "filter", $"{locationHint}.filter", issues);

        if (runTaskDefinition.Inline?.Do is not { Count: > 0 })
        {
            return;
        }

        var nestedValidation = new WorkflowDefinitionValidator().Validate(new WorkflowDefinition
        {
            Do = runTaskDefinition.Inline.Do.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal)
        });

        foreach (var issue in nestedValidation.Issues)
        {
            issues.Add(issue);
        }

        callableTasks += CountCallableTasks(runTaskDefinition.Inline.Do.Values);
    }

    public void ValidateOrThrow(WorkflowDefinition definition)
    {
        var validation = Validate(definition);
        if (validation.IsValid)
        {
            return;
        }

        var combinedMessage = string.Join(Environment.NewLine, validation.Issues.Select(issue => issue.Message));
        throw new InvalidOperationException(combinedMessage);
    }

    private static void ValidateSwitchBranches(
        SwitchTaskDefinition switchTaskDefinition,
        string taskName,
        string locationHint,
        ICollection<WorkflowDefinitionValidationIssue> issues,
        ISet<string> referencedTaskNames)
    {
        var branches = SwitchTaskAdapter.ReadBranches(switchTaskDefinition, locationHint);

        var defaultBranchCount = 0;

        foreach (var branch in branches)
        {
            if (branch.IsDefault)
            {
                defaultBranchCount++;
                if (defaultBranchCount > 1)
                {
                    issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {locationHint} defines more than one default branch. Only one 'default' is allowed."));
                }

                if (!string.IsNullOrWhiteSpace(branch.When))
                {
                    issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {branch.LocationHint} mixes 'default' with branch conditions. Put default in its own switch item."));
                }

                if (branch.Then is null
                    || branch.Then is string defaultText && string.IsNullOrWhiteSpace(defaultText)
                    || SwitchTaskAdapter.IsNullDirective(branch.Then))
                {
                    issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {branch.LocationHint}.default must point to a task, inline task block, or use 'then: {SwitchTaskAdapter.EndDirective}' to end the current flow."));
                }

                if (!SwitchTaskAdapter.IsEndDirective(branch.Then) && !SwitchTaskAdapter.IsNullDirective(branch.Then))
                {
                    CollectReferencedTaskNames(branch.Then, taskName, branch.LocationHint, referencedTaskNames, issues);
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(branch.When))
            {
                issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {branch.LocationHint} is missing 'when'. Conditional switch branches require a 'when' expression."));
            }

            if (branch.Then is null
                || branch.Then is string thenText && string.IsNullOrWhiteSpace(thenText)
                || SwitchTaskAdapter.IsNullDirective(branch.Then))
            {
                issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {branch.LocationHint} is missing 'then'. Add a named target or use 'then: {SwitchTaskAdapter.EndDirective}' to end the current flow."));
            }

            if (!string.IsNullOrWhiteSpace(branch.When))
            {
                try
                {
                    WorkflowConditionEvaluator.ValidateSyntax(branch.When, new JsonObject());
                }
                catch (Exception ex)
                {
                    issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' has an invalid switch expression at {branch.LocationHint}.when: {ex.Message}"));
                }
            }

            if (SwitchTaskAdapter.IsEndDirective(branch.Then) || SwitchTaskAdapter.IsNullDirective(branch.Then))
            {
                continue;
            }

            var switchThenTargets = NormalizeThenTargets(branch.Then, taskName, branch.LocationHint, issues);
            if (switchThenTargets is not null)
            {
                if (switchThenTargets.Count != 1 || switchThenTargets[0] is not NamedTaskTarget)
                {
                    issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {branch.LocationHint} uses an unsupported switch pattern. Each branch must define exactly one named 'then' task."));
                }
            }

            CollectReferencedTaskNames(branch.Then, taskName, branch.LocationHint, referencedTaskNames, issues);
        }
    }



    private static void ValidateDoTask(
        DoTaskDefinition doTaskDefinition,
        string taskName,
        string locationHint,
        ICollection<WorkflowDefinitionValidationIssue> issues,
        ref int callableTasks)
    {
        var ifLocationHint = $"{locationHint}.if";
        var filterLocationHint = $"{locationHint}.filter";
        var whileLocationHint = $"{locationHint}.while";
        var hasFor = doTaskDefinition.ForIn is not null
            || doTaskDefinition.ForEach is not null
            || doTaskDefinition.ForAt is not null
            || doTaskDefinition.ForSize is not null
            || doTaskDefinition.ForMaxConcurrency is not null
            || doTaskDefinition.ForInput is not null
            || doTaskDefinition.ForCollect is not null
            || doTaskDefinition.ForCollectInclude is not null
            || doTaskDefinition.ForOnError is not null;

        if (!string.IsNullOrWhiteSpace(doTaskDefinition.If) && !string.IsNullOrWhiteSpace(doTaskDefinition.Filter))
        {
            issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {locationHint} defines both 'if' and 'filter'. Do tasks support only one pre-condition field."));
        }

        ValidateConditionExpression(doTaskDefinition.If, taskName, "if", ifLocationHint, issues);
        ValidateConditionExpression(doTaskDefinition.Filter, taskName, "filter", filterLocationHint, issues);

        if (doTaskDefinition.Do.Count == 0)
        {
            issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {locationHint} must define at least one nested task in 'do'."));
            return;
        }

        if (doTaskDefinition.While is not null && hasFor)
        {
            issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {whileLocationHint} cannot combine 'while' with 'for'. Use exactly one loop mode for do tasks."));
            issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {locationHint}.for cannot combine 'for' with 'while'. Use exactly one loop mode for do tasks."));
        }

        if (hasFor)
        {
            var forLocationHint = $"{locationHint}.for";

            if (string.IsNullOrWhiteSpace(doTaskDefinition.ForIn))
            {
                issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {forLocationHint}.in must define a non-empty source expression when 'for' is present."));
            }

            if (string.IsNullOrWhiteSpace(doTaskDefinition.ForEach))
            {
                issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {forLocationHint}.each must define a non-empty item variable when 'for' is present."));
            }

            if (doTaskDefinition.ForAt is not null && string.IsNullOrWhiteSpace(doTaskDefinition.ForAt))
            {
                issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {forLocationHint}.at must be a non-empty index variable when provided."));
            }

            if (doTaskDefinition.ForSize is <= 0)
            {
                issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {forLocationHint}.size must be a positive integer when provided."));
            }

            if (doTaskDefinition.ForMaxConcurrency is <= 0)
            {
                issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {forLocationHint}.maxConcurrency must be a positive integer when provided."));
            }

            if (doTaskDefinition.ForInput is not null && doTaskDefinition.ForMaxConcurrency is null)
            {
                issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {forLocationHint}.input is supported only when maxConcurrency is provided."));
            }

            if (doTaskDefinition.ForCollectInclude is not null)
            {
                if (doTaskDefinition.ForMaxConcurrency is null)
                {
                    issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {forLocationHint}.collect.include is supported only when maxConcurrency is provided."));
                }

                var allowedCollectFields = new HashSet<string>(["index", "success", "item", "output", "error"], StringComparer.OrdinalIgnoreCase);
                if (doTaskDefinition.ForCollectInclude.Count == 0)
                {
                    issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {forLocationHint}.collect.include must contain at least one result field."));
                }

                foreach (var field in doTaskDefinition.ForCollectInclude.Where(field => !allowedCollectFields.Contains(field)))
                {
                    issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {forLocationHint}.collect.include contains unsupported field '{field}'."));
                }

                if (doTaskDefinition.ForCollectInclude.Distinct(StringComparer.OrdinalIgnoreCase).Count() != doTaskDefinition.ForCollectInclude.Count)
                {
                    issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {forLocationHint}.collect.include cannot contain duplicate fields."));
                }
            }

            if (doTaskDefinition.ForMaxConcurrency is not null && string.IsNullOrWhiteSpace(doTaskDefinition.ForCollect))
            {
                issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {forLocationHint}.collect must define a non-empty result variable when maxConcurrency is provided."));
            }

            if (!string.IsNullOrWhiteSpace(doTaskDefinition.ForOnError)
                && !string.Equals(doTaskDefinition.ForOnError, "failFast", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(doTaskDefinition.ForOnError, "collect", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {forLocationHint}.onError must be either 'failFast' or 'collect'."));
            }
        }

        ValidateGuard(taskName, locationHint, doTaskDefinition.Guard, issues);

        if (doTaskDefinition.Guard is not null && doTaskDefinition.While is null && !hasFor)
        {
            issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {locationHint}.guard is unsupported unless the do task defines either 'while' or 'for'."));
        }

        if (doTaskDefinition.While is not null)
        {
            if (string.IsNullOrWhiteSpace(doTaskDefinition.While))
            {
                issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {whileLocationHint} must define a non-empty while expression when 'while' is present."));
            }
            else
            {
                ValidateConditionExpression(doTaskDefinition.While, taskName, "while", whileLocationHint, issues);
            }
        }

        var nestedValidation = new WorkflowDefinitionValidator().Validate(new WorkflowDefinition
        {
            Do = doTaskDefinition.Do.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal)
        });

        foreach (var issue in nestedValidation.Issues)
        {
            issues.Add(issue);
        }

        callableTasks += CountCallableTasks(doTaskDefinition.Do.Values);
    }


    private static void ValidateForkTask(
        ForkTaskDefinition forkTaskDefinition,
        string taskName,
        string locationHint,
        ICollection<WorkflowDefinitionValidationIssue> issues,
        ref int callableTasks)
    {
        var forkLocationHint = $"{locationHint}.fork";
        if (forkTaskDefinition.Branches.Count == 0)
        {
            issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {forkLocationHint}.branches must define at least one branch."));
            return;
        }

        var duplicateBranchNames = forkTaskDefinition.Branches
            .GroupBy(branch => branch.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        foreach (var duplicateBranchName in duplicateBranchNames)
        {
            issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {forkLocationHint}.branches defines duplicate branch name '{duplicateBranchName}'."));
        }

        foreach (var branch in forkTaskDefinition.Branches)
        {
            if (branch.Do.Count == 0)
            {
                issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {forkLocationHint}.branches['{branch.Name}'].do must define non-empty tasks."));
                continue;
            }

            var nestedValidation = new WorkflowDefinitionValidator().Validate(new WorkflowDefinition
            {
                Do = branch.Do.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal)
            });

            foreach (var issue in nestedValidation.Issues)
            {
                issues.Add(issue);
            }

            callableTasks += CountCallableTasks(branch.Do.Values);
        }

        if (!string.IsNullOrWhiteSpace(forkTaskDefinition.Join?.Mode) && !string.Equals(forkTaskDefinition.Join.Mode, "all", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {forkLocationHint}.join.mode supports only 'all' in this release."));
        }
    }

    private static void ValidateTryTask(
        string taskName,
        string locationHint,
        TryTaskDefinition tryTaskDefinition,
        ICollection<WorkflowDefinitionValidationIssue> issues,
        ref int callableTasks)
    {
        if (tryTaskDefinition.Try.Count == 0)
        {
            issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {locationHint} must define at least one nested task in 'try'."));
            return;
        }

        var nestedValidation = new WorkflowDefinitionValidator().Validate(new WorkflowDefinition
        {
            Do = tryTaskDefinition.Try.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal)
        });

        foreach (var issue in nestedValidation.Issues)
        {
            issues.Add(issue);
        }

        foreach (var catchDefinition in tryTaskDefinition.Catch)
        {
            if (catchDefinition.Do.Count == 0)
            {
                issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {locationHint}.catch must define non-empty do tasks."));
                continue;
            }

            var catchValidation = new WorkflowDefinitionValidator().Validate(new WorkflowDefinition
            {
                Do = catchDefinition.Do.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal)
            });

            foreach (var issue in catchValidation.Issues)
            {
                issues.Add(issue);
            }
        }

        if (tryTaskDefinition.Finally is { Count: > 0 })
        {
            var finallyValidation = new WorkflowDefinitionValidator().Validate(new WorkflowDefinition
            {
                Do = tryTaskDefinition.Finally.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal)
            });

            foreach (var issue in finallyValidation.Issues)
            {
                issues.Add(issue);
            }
        }

        callableTasks += CountCallableTasks(tryTaskDefinition.Try.Values);
        foreach (var c in tryTaskDefinition.Catch)
        {
            callableTasks += CountCallableTasks(c.Do.Values);
        }

        if (tryTaskDefinition.Finally is { Count: > 0 })
        {
            callableTasks += CountCallableTasks(tryTaskDefinition.Finally.Values);
        }
    }

    private static void ValidateConditionExpression(
        string? expression,
        string taskName,
        string fieldName,
        string locationHint,
        ICollection<WorkflowDefinitionValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return;
        }

        try
        {
            WorkflowConditionEvaluator.ValidateSyntax(expression, new JsonObject());
        }
        catch (Exception ex)
        {
            issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' has an invalid {fieldName} expression at {locationHint}: {ex.Message}"));
        }
    }

    private static void ValidateGuard(
        string taskName,
        string locationHint,
        LoopGuardPolicyDefinition? guard,
        ICollection<WorkflowDefinitionValidationIssue> issues)
    {
        if (guard is null)
        {
            return;
        }

        if (guard.MaxIterations is <= 0)
        {
            issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {locationHint}.guard.maxIterations must be greater than zero when provided."));
        }

        if (guard.MaxRepeatedPayloads is <= 0)
        {
            issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {locationHint}.guard.maxRepeatedPayloads must be greater than zero when provided."));
        }

        if (guard.TimeoutMs is <= 0)
        {
            issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {locationHint}.guard.timeoutMs must be greater than zero when provided."));
        }

        if (!string.IsNullOrWhiteSpace(guard.OnCancel)
            && !string.Equals(guard.OnCancel.Trim(), "fail", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(guard.OnCancel.Trim(), "exit", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {locationHint}.guard.onCancel has unsupported value '{guard.OnCancel}'. Supported values are 'fail' and 'exit'."));
        }
    }

    private static int CountCallableTasks(IEnumerable<TaskDefinition> taskDefinitions)
    {
        var total = 0;

        foreach (var taskDefinition in taskDefinitions)
        {
            switch (taskDefinition)
            {
                case CallTaskDefinition:
                case MapTaskDefinition:
                case StashTaskDefinition:
                case TerminateTaskDefinition:
                    total++;
                    break;
                case RunTaskDefinition runTask:
                    total++;
                    if (runTask.Inline is { Do.Count: > 0 })
                    {
                        total += CountCallableTasks(runTask.Inline.Do.Values);
                    }
                    break;
                case DoTaskDefinition nestedDoTask:
                    total += CountCallableTasks(nestedDoTask.Do.Values);
                    break;
                case ForkTaskDefinition forkTask:
                    foreach (var branch in forkTask.Branches)
                    {
                        total += CountCallableTasks(branch.Do.Values);
                    }
                    break;
                case TryTaskDefinition tryTask:
                    total += CountCallableTasks(tryTask.Try.Values);
                    foreach (var c in tryTask.Catch)
                    {
                        total += CountCallableTasks(c.Do.Values);
                    }
                    if (tryTask.Finally is { Count: > 0 })
                    {
                        total += CountCallableTasks(tryTask.Finally.Values);
                    }
                    break;
            }
        }

        return total;
    }

    private static void ValidateRaiseTask(
        RaiseTaskDefinition raiseTaskDefinition,
        string taskName,
        string locationHint,
        ICollection<WorkflowDefinitionValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(raiseTaskDefinition.ErrorType))
        {
            issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {locationHint}.raise.error must define a non-empty error type."));
        }

        ValidateConditionExpression(raiseTaskDefinition.If, taskName, "if", $"{locationHint}.if", issues);
        ValidateConditionExpression(raiseTaskDefinition.Filter, taskName, "filter", $"{locationHint}.filter", issues);
    }

    private static void ValidateTerminateTask(
        TerminateTaskDefinition terminateTaskDefinition,
        string taskName,
        string locationHint,
        ICollection<WorkflowDefinitionValidationIssue> issues)
    {
        if (!string.IsNullOrWhiteSpace(terminateTaskDefinition.Then?.ToString()))
        {
            issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {locationHint} cannot define 'then' because terminate tasks are always terminal."));
        }

        if (!string.IsNullOrWhiteSpace(terminateTaskDefinition.If) && !string.IsNullOrWhiteSpace(terminateTaskDefinition.Filter))
        {
            issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {locationHint} defines both 'if' and 'filter'. Terminate tasks support only one pre-condition field."));
        }

        ValidateConditionExpression(terminateTaskDefinition.If, taskName, "if", $"{locationHint}.if", issues);
        ValidateConditionExpression(terminateTaskDefinition.Filter, taskName, "filter", $"{locationHint}.filter", issues);

        if (string.IsNullOrWhiteSpace(terminateTaskDefinition.Status))
        {
            issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {locationHint}.terminate.status must define a non-empty terminal status."));
        }
        else if (!IsSupportedTerminateStatus(terminateTaskDefinition.Status))
        {
            issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {locationHint}.terminate.status has unsupported value '{terminateTaskDefinition.Status}'. Supported values are Completed, Cancelled, and Failed."));
        }

        if (terminateTaskDefinition.Output is not null && terminateTaskDefinition.Output is not System.Collections.IDictionary)
        {
            issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {locationHint}.terminate.output must be an object mapping when provided."));
        }
    }

    private static void ValidateEmitTask(
        EmitTaskDefinition emitTaskDefinition,
        string taskName,
        string locationHint,
        ICollection<WorkflowDefinitionValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(emitTaskDefinition.EventType))
        {
            issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {locationHint}.emit must define a non-empty event type using 'event' or 'type'."));
        }

        if (!string.IsNullOrWhiteSpace(emitTaskDefinition.If) && !string.IsNullOrWhiteSpace(emitTaskDefinition.Filter))
        {
            issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {locationHint} defines both 'if' and 'filter'. Emit tasks support only one pre-condition field."));
        }
    }

    private static void ValidateMapTask(
        MapTaskDefinition mapTaskDefinition,
        string taskName,
        string locationHint,
        ICollection<WorkflowDefinitionValidationIssue> issues)
    {
        if (!string.IsNullOrWhiteSpace(mapTaskDefinition.If) && !string.IsNullOrWhiteSpace(mapTaskDefinition.Filter))
        {
            issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {locationHint} defines both 'if' and 'filter'. Map tasks support only one pre-condition field."));
        }

        ValidateConditionExpression(mapTaskDefinition.If, taskName, "if", $"{locationHint}.if", issues);
        ValidateConditionExpression(mapTaskDefinition.Filter, taskName, "filter", $"{locationHint}.filter", issues);

        if (mapTaskDefinition.Map.Rules.Count == 0)
        {
            issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {locationHint}.map.rules must define at least one rule."));
        }
    }

    private static void ValidateStashTask(
        StashTaskDefinition stashTaskDefinition,
        string taskName,
        string locationHint,
        ICollection<WorkflowDefinitionValidationIssue> issues)
    {
        if (stashTaskDefinition.Values is null)
        {
            issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {locationHint}.stash must define an object."));
        }

        if (!string.IsNullOrWhiteSpace(stashTaskDefinition.If) && !string.IsNullOrWhiteSpace(stashTaskDefinition.Filter))
        {
            issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {locationHint} defines both 'if' and 'filter'. Stash tasks support only one pre-condition field."));
        }

        ValidateConditionExpression(stashTaskDefinition.If, taskName, "if", $"{locationHint}.if", issues);
        ValidateConditionExpression(stashTaskDefinition.Filter, taskName, "filter", $"{locationHint}.filter", issues);
    }

    private static void ValidateWaitTask(
        WaitTaskDefinition waitTaskDefinition,
        string taskName,
        string locationHint,
        ICollection<WorkflowDefinitionValidationIssue> issues)
    {
        var hasFor = !string.IsNullOrWhiteSpace(waitTaskDefinition.For);
        var hasUntil = !string.IsNullOrWhiteSpace(waitTaskDefinition.Until);
        var waitLocationHint = $"{locationHint}.wait";

        if (hasFor == hasUntil)
        {
            issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {waitLocationHint} must define exactly one of 'for' or 'until'."));
            return;
        }

        if (hasFor)
        {
            try
            {
                var duration = XmlConvert.ToTimeSpan(waitTaskDefinition.For!);
                if (duration <= TimeSpan.Zero)
                {
                    issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {waitLocationHint}.for must be greater than zero."));
                }
            }
            catch (Exception ex)
            {
                issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' has an invalid wait duration at {waitLocationHint}.for: {ex.Message}"));
            }
        }

        if (hasUntil && !DateTimeOffset.TryParse(waitTaskDefinition.Until, out _))
        {
            issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' has an invalid wait timestamp at {waitLocationHint}.until."));
        }

        var normalizedTargets = NormalizeThenTargets(waitTaskDefinition.Then, taskName, locationHint, issues);
        if (normalizedTargets is null)
        {
            return;
        }

        if (normalizedTargets.Count > 1 || normalizedTargets.Any(static target => target is not NamedTaskTarget))
        {
            issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {locationHint} uses an unsupported wait pattern. Wait tasks support at most one named 'then' target."));
        }
    }

    private static void ValidateListenTask(
        ListenTaskDefinition listenTaskDefinition,
        string taskName,
        string locationHint,
        ICollection<WorkflowDefinitionValidationIssue> issues)
    {
        var normalizedTargets = NormalizeThenTargets(listenTaskDefinition.Then, taskName, locationHint, issues);
        if (normalizedTargets is null)
        {
            return;
        }

        if (normalizedTargets.Count > 1 || normalizedTargets.Any(static target => target is not NamedTaskTarget))
        {
            issues.Add(new WorkflowDefinitionValidationIssue($"Task '{taskName}' at {locationHint} uses an unsupported listen pattern. Listen tasks support at most one named 'then' target."));
        }
    }

    private static List<ThenTargetDescriptor>? NormalizeThenTargets(
        object? thenValue,
        string taskName,
        string locationHint,
        ICollection<WorkflowDefinitionValidationIssue> issues)
    {
        var normalizer = new ThenTargetNormalizer((parentName, displayName) => $"{parentName}.__validation_{displayName}");

        try
        {
            return normalizer.Normalize(thenValue, taskName, locationHint).ToList();
        }
        catch (InvalidOperationException ex)
        {
            issues.Add(new WorkflowDefinitionValidationIssue(ex.Message));
            return null;
        }
    }

    private static void CollectReferencedTaskNames(
        object? thenValue,
        string taskName,
        string locationHint,
        ISet<string> referencedTaskNames,
        ICollection<WorkflowDefinitionValidationIssue> issues)
    {
        var normalizer = new ThenTargetNormalizer((parentName, displayName) => $"{parentName}.__validation_{displayName}");

        try
        {
            foreach (var target in normalizer.Normalize(thenValue, taskName, locationHint))
            {
                switch (target)
                {
                    case NamedTaskTarget namedTaskTarget:
                        referencedTaskNames.Add(namedTaskTarget.Name);
                        break;
                    case InlineTaskTarget inlineTaskTarget:
                        var inlineThen = inlineTaskTarget.InlineDefinition["then"] ?? inlineTaskTarget.InlineDefinition["Then"];
                        CollectReferencedTaskNames(inlineThen, inlineTaskTarget.InternalName, inlineTaskTarget.LocationHint, referencedTaskNames, issues);
                        break;
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            issues.Add(new WorkflowDefinitionValidationIssue(ex.Message));
        }
    }

    private static string BuildLocationHint(string taskName)
    {
        return $"document.do['{taskName}']";
    }

    private static bool IsSupportedTerminateStatus(string? status)
    {
        return status switch
        {
            WorkflowInstanceStatuses.Completed => true,
            WorkflowInstanceStatuses.Cancelled => true,
            WorkflowInstanceStatuses.Failed => true,
            _ => false
        };
    }
}
