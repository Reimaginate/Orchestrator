using System.Text.Json.Nodes;
using System.Xml;
using Reimaginate.Orchestrator.Common.Helpers;
using Reimaginate.Orchestrator.Common.Constants;
using Reimaginate.Orchestrator.Common.Requests.Internal.BuildAgentWorkflow;
using Reimaginate.Orchestrator.Common.Services.WorkflowDefinitions;
using Reimaginate.Orchestrator.DSL;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowDefinitions.Compilation;

internal sealed class WorkflowSemanticValidator
{
    public WorkflowSemanticValidationResult Validate(WorkflowAst ast)
    {
        ArgumentNullException.ThrowIfNull(ast);

        var diagnostics = new List<WorkflowCompilationDiagnostic>();
        if (ast.Tasks.Count == 0)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic("Workflow definition does not contain any tasks.", "document"));
            return new WorkflowSemanticValidationResult(null, diagnostics);
        }

        var duplicateNames = ast.Tasks
            .GroupBy(task => task.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        foreach (var duplicateName in duplicateNames)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{duplicateName}' is defined more than once.", BuildLocationHint(duplicateName)));
        }

        var definition = WorkflowAstBinder.Bind(ast);
        return Validate(definition, ast.Tasks.Select(x => x.Name).Distinct(StringComparer.Ordinal).ToArray(), diagnostics);
    }

    public WorkflowSemanticValidationResult Validate(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return Validate(definition, definition.Do.Keys.ToArray(), new List<WorkflowCompilationDiagnostic>());
    }

    private static HashSet<string> ValidateExtensions(WorkflowDefinition definition, ICollection<WorkflowCompilationDiagnostic> diagnostics)
    {
        var extensionNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var extension in definition.Use?.Extensions ?? [])
        {
            var locationHint = BuildExtensionLocationHint(extension.Name);
            if (!extensionNames.Add(extension.Name))
            {
                diagnostics.Add(new WorkflowCompilationDiagnostic($"Extension '{extension.Name}' is defined more than once.", locationHint));
            }

            if (!string.IsNullOrWhiteSpace(extension.Extend)
                && !string.Equals(extension.Extend, "all", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new WorkflowCompilationDiagnostic($"Extension '{extension.Name}' has unsupported extend target '{extension.Extend}'. Only 'all' is supported.", $"{locationHint}.extend"));
            }

            ValidateExtensionHook(extension.Name, "before", extension.Before, extension.BeforeWhen, diagnostics);
            ValidateExtensionHook(extension.Name, "after", extension.After, extension.AfterWhen, diagnostics);
            ValidateExtensionHook(extension.Name, "onError", extension.OnError, extension.OnErrorWhen, diagnostics);
        }

        return extensionNames;
    }

    private static void ValidateExtensionHook(
        string extensionName,
        string hookName,
        WorkflowTaskBlockDefinition? taskBlock,
        string? when,
        ICollection<WorkflowCompilationDiagnostic> diagnostics)
    {
        ValidateConditionExpression(extensionName, $"{hookName}.when", when, $"{BuildExtensionLocationHint(extensionName)}.{hookName}.when", diagnostics);

        if (taskBlock is null)
        {
            return;
        }

        var locationHint = $"{BuildExtensionLocationHint(extensionName)}.{hookName}";
        var duplicateTaskNames = taskBlock.TaskOrder
            .GroupBy(name => name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        foreach (var duplicateTaskName in duplicateTaskNames)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic(
                $"Extension '{extensionName}' defines task '{duplicateTaskName}' more than once in '{hookName}'.",
                locationHint));
        }

        var hookTaskNames = taskBlock.Do.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var task in taskBlock.Do)
        {
            if (task.Value.Extend.Count > 0)
            {
                diagnostics.Add(new WorkflowCompilationDiagnostic(
                    $"Extension '{extensionName}' task '{task.Key}' in '{hookName}' cannot declare 'extend'.",
                    $"{locationHint}.do['{task.Key}'].extend"));
            }

            ValidateTask(
                task.Key,
                task.Value,
                hookTaskNames,
                new HashSet<string>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal),
                diagnostics,
                locationHint,
                allowUnknownThenTargetsOutsideScope: false);
        }
    }

    private static void ValidateTask(
        string taskName,
        TaskDefinition definition,
        IReadOnlySet<string> definedTaskNames,
        IReadOnlySet<string> extensionNames,
        IReadOnlySet<string> globalExtensionNames,
        ICollection<WorkflowCompilationDiagnostic> diagnostics,
        string? locationHintOverride = null,
        bool allowUnknownThenTargetsOutsideScope = true)
    {
        var locationHint = locationHintOverride is null ? BuildLocationHint(taskName) : $"{locationHintOverride}.do['{taskName}']";
        var references = CollectThenTargets(definition.Then, taskName, locationHint, diagnostics);

        foreach (var extensionName in definition.Extend)
        {
            if (!extensionNames.Contains(extensionName))
            {
                diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' references unknown extension '{extensionName}'.", $"{locationHint}.extend"));
            }
        }

        if (definition.Extend.Count > 0 && !WorkflowExtensionWrappingSupport.SupportsJsonHookWrapping(definition))
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic(
                $"Task '{taskName}' at {locationHint} cannot use extensions because its executor does not support JSON hook wrapping.",
                locationHint));
        }

        foreach (var referenced in references)
        {
            if (!definedTaskNames.Contains(referenced))
            {
                diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' references missing then target '{referenced}'.", locationHint));
            }
        }

        if (definition is RunTaskDefinition runTask)
        {
            ValidateRunTask(taskName, locationHint, runTask, diagnostics, extensionNames, globalExtensionNames);
        }

        if (definition is SwitchTaskDefinition switchTask)
        {
            ValidateSwitchTask(taskName, locationHint, switchTask, definedTaskNames, diagnostics);
        }

        if (definition is DoTaskDefinition doTask)
        {
            ValidateDoTask(taskName, locationHint, doTask, diagnostics, extensionNames, globalExtensionNames);
        }

        if (definition is ForkTaskDefinition forkTask)
        {
            ValidateForkTask(taskName, locationHint, forkTask, diagnostics, extensionNames, globalExtensionNames);
        }

        if (definition is WaitTaskDefinition waitTask)
        {
            ValidateWaitTask(taskName, locationHint, waitTask, diagnostics);
        }

        if (definition is TerminateTaskDefinition terminateTask)
        {
            ValidateTerminateTask(taskName, locationHint, terminateTask, diagnostics);
        }

        if (definition is RaiseTaskDefinition raiseTask)
        {
            ValidateRaiseTask(taskName, locationHint, raiseTask, diagnostics);
        }

        if (definition is EmitTaskDefinition emitTask)
        {
            ValidateEmitTask(taskName, locationHint, emitTask, diagnostics);
        }

        if (definition is MapTaskDefinition mapTask)
        {
            ValidateMapTask(taskName, locationHint, mapTask, diagnostics);
        }

        if (definition is StashTaskDefinition stashTask)
        {
            ValidateStashTask(taskName, locationHint, stashTask, diagnostics);
        }

        if (definition is ListenTaskDefinition listenTask)
        {
            ValidateListenTask(taskName, locationHint, listenTask, diagnostics);
        }

        if (definition is TryTaskDefinition tryTask)
        {
            ValidateTryTask(taskName, locationHint, tryTask, diagnostics, extensionNames, globalExtensionNames);
        }
    }

    private static void ValidateRunTask(
        string taskName,
        string locationHint,
        RunTaskDefinition runTask,
        ICollection<WorkflowCompilationDiagnostic> diagnostics,
        IReadOnlySet<string> extensionNames,
        IReadOnlySet<string> globalExtensionNames)
    {
        if (string.IsNullOrWhiteSpace(runTask.Workflow.WorkflowType))
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint}.run.workflow.workflowType must define a non-empty workflow type.", $"{locationHint}.run.workflow.workflowType"));
        }

        ValidateConditionExpression(taskName, "if", runTask.If, $"{locationHint}.if", diagnostics);
        ValidateConditionExpression(taskName, "filter", runTask.Filter, $"{locationHint}.filter", diagnostics);

        if (runTask.Inline?.Do is not { Count: > 0 })
        {
            return;
        }

        ValidateNestedTasks(runTask.Inline.Do, $"{locationHint}.run.workflow", extensionNames, globalExtensionNames, diagnostics);
    }


    private static void ValidateSwitchTask(
        string taskName,
        string locationHint,
        SwitchTaskDefinition switchTask,
        IReadOnlySet<string> definedTaskNames,
        ICollection<WorkflowCompilationDiagnostic> diagnostics)
    {
        var branches = SwitchTaskAdapter.ReadBranches(switchTask, locationHint);
        if (branches.Count == 0)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint} does not define any switch branches.", locationHint));
            return;
        }

        var defaultBranchCount = 0;
        foreach (var branch in branches)
        {
            if (branch.IsDefault)
            {
                defaultBranchCount++;

                if (!string.IsNullOrWhiteSpace(branch.When))
                {
                    diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {branch.LocationHint} mixes 'default' with branch conditions. Put default in its own switch item.", branch.LocationHint));
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(branch.When))
                {
                    diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {branch.LocationHint} is missing 'when'. Conditional switch branches require a 'when' expression.", $"{branch.LocationHint}.when"));
                }
                else
                {
                    ValidateConditionExpression(taskName, "switch", branch.When, $"{branch.LocationHint}.when", diagnostics);
                }
            }

            if (branch.Then is null
                || branch.Then is string thenText && string.IsNullOrWhiteSpace(thenText)
                || SwitchTaskAdapter.IsNullDirective(branch.Then))
            {
                diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {branch.LocationHint} is missing 'then'. Add a named target or use 'then: {SwitchTaskAdapter.EndDirective}' to end the current flow.", $"{branch.LocationHint}.then"));
                continue;
            }

            if (SwitchTaskAdapter.IsEndDirective(branch.Then))
            {
                continue;
            }

            var branchTarget = TryGetSingleNamedTarget(branch.Then, taskName, branch.LocationHint, diagnostics);
            if (branchTarget is null)
            {
                diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' has invalid branch target at {branch.LocationHint}. Expected exactly one named then target.", branch.LocationHint));
                continue;
            }

            if (!definedTaskNames.Contains(branchTarget))
            {
                diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' switch branch points to missing target '{branchTarget}'.", branch.LocationHint));
            }
        }

        if (defaultBranchCount > 1)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint} defines more than one default switch branch.", locationHint));
        }

        ValidateConditionExpression(taskName, "if", switchTask.If, $"{locationHint}.if", diagnostics);
        ValidateConditionExpression(taskName, "filter", switchTask.Filter, $"{locationHint}.filter", diagnostics);
    }

    private static void ValidateEmitTask(
        string taskName,
        string locationHint,
        EmitTaskDefinition emitTask,
        ICollection<WorkflowCompilationDiagnostic> diagnostics)
    {
        var emitLocationHint = $"{locationHint}.emit";

        if (string.IsNullOrWhiteSpace(emitTask.EventType))
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {emitLocationHint}.event must define a non-empty event type.", $"{emitLocationHint}.event"));
        }

        ValidateConditionExpression(taskName, "if", emitTask.If, $"{locationHint}.if", diagnostics);
        ValidateConditionExpression(taskName, "filter", emitTask.Filter, $"{locationHint}.filter", diagnostics);
    }

    private static void ValidateRaiseTask(
        string taskName,
        string locationHint,
        RaiseTaskDefinition raiseTask,
        ICollection<WorkflowCompilationDiagnostic> diagnostics)
    {
        var raiseLocationHint = $"{locationHint}.raise";

        if (string.IsNullOrWhiteSpace(raiseTask.ErrorType))
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {raiseLocationHint}.error must define a non-empty error type.", $"{raiseLocationHint}.error"));
        }

        ValidateConditionExpression(taskName, "if", raiseTask.If, $"{locationHint}.if", diagnostics);
        ValidateConditionExpression(taskName, "filter", raiseTask.Filter, $"{locationHint}.filter", diagnostics);
    }

    private static void ValidateTerminateTask(
        string taskName,
        string locationHint,
        TerminateTaskDefinition terminateTask,
        ICollection<WorkflowCompilationDiagnostic> diagnostics)
    {
        var terminateLocationHint = $"{locationHint}.terminate";

        if (terminateTask.Then is not null)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint} cannot define 'then' because terminate tasks are always terminal.", locationHint));
        }

        if (!string.IsNullOrWhiteSpace(terminateTask.If) && !string.IsNullOrWhiteSpace(terminateTask.Filter))
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint} defines both 'if' and 'filter'. Terminate tasks support only one pre-condition field.", locationHint));
        }

        if (string.IsNullOrWhiteSpace(terminateTask.Status))
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {terminateLocationHint}.status must define a non-empty terminal status.", $"{terminateLocationHint}.status"));
        }
        else if (!IsSupportedTerminateStatus(terminateTask.Status))
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {terminateLocationHint}.status has unsupported value '{terminateTask.Status}'. Supported values are Completed, Cancelled, and Failed.", $"{terminateLocationHint}.status"));
        }

        if (terminateTask.Output is not null && terminateTask.Output is not System.Collections.IDictionary)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {terminateLocationHint}.output must be an object mapping when provided.", $"{terminateLocationHint}.output"));
        }

        ValidateConditionExpression(taskName, "if", terminateTask.If, $"{locationHint}.if", diagnostics);
        ValidateConditionExpression(taskName, "filter", terminateTask.Filter, $"{locationHint}.filter", diagnostics);
    }

    private static void ValidateStashTask(
        string taskName,
        string locationHint,
        StashTaskDefinition stashTask,
        ICollection<WorkflowCompilationDiagnostic> diagnostics)
    {
        if (stashTask.Values is null)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint}.stash must define an object.", $"{locationHint}.stash"));
        }

        if (!string.IsNullOrWhiteSpace(stashTask.If) && !string.IsNullOrWhiteSpace(stashTask.Filter))
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint} defines both 'if' and 'filter'. Stash tasks support only one pre-condition field.", locationHint));
        }

        ValidateConditionExpression(taskName, "if", stashTask.If, $"{locationHint}.if", diagnostics);
        ValidateConditionExpression(taskName, "filter", stashTask.Filter, $"{locationHint}.filter", diagnostics);
    }

    private static void ValidateMapTask(
        string taskName,
        string locationHint,
        MapTaskDefinition mapTask,
        ICollection<WorkflowCompilationDiagnostic> diagnostics)
    {
        if (!string.IsNullOrWhiteSpace(mapTask.If) && !string.IsNullOrWhiteSpace(mapTask.Filter))
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint} defines both 'if' and 'filter'. Map tasks support only one pre-condition field.", locationHint));
        }

        ValidateConditionExpression(taskName, "if", mapTask.If, $"{locationHint}.if", diagnostics);
        ValidateConditionExpression(taskName, "filter", mapTask.Filter, $"{locationHint}.filter", diagnostics);
        ValidateMapDefinition(taskName, mapTask.Map, $"{locationHint}.map", diagnostics);
    }

    private static void ValidateMapDefinition(
        string taskName,
        MapDefinition mapDefinition,
        string locationHint,
        ICollection<WorkflowCompilationDiagnostic> diagnostics)
    {
        if (mapDefinition.Schema is not null)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint}.schema is not supported in this release.", $"{locationHint}.schema"));
        }

        if (mapDefinition.Options is not null)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint}.options is not supported in this release.", $"{locationHint}.options"));
        }

        if (mapDefinition.Input is not null)
        {
            if (string.IsNullOrWhiteSpace(mapDefinition.Input))
            {
                diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint}.input must be a non-empty source expression when provided.", $"{locationHint}.input"));
            }
            else
            {
                ValidateJsonPathExpression(taskName, "map input", mapDefinition.Input, $"{locationHint}.input", diagnostics);
            }
        }

        if (mapDefinition.Rules.Count == 0)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint}.rules must define at least one rule.", $"{locationHint}.rules"));
            return;
        }

        for (var index = 0; index < mapDefinition.Rules.Count; index++)
        {
            ValidateMapRule(taskName, mapDefinition.Rules[index], $"{locationHint}.rules[{index}]", diagnostics);
        }
    }

    private static void ValidateMapRule(
        string taskName,
        MapRuleDefinition rule,
        string locationHint,
        ICollection<WorkflowCompilationDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(rule.Target))
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint}.target must define a non-empty target path.", $"{locationHint}.target"));
        }
        else
        {
            try
            {
                var parsedTarget = WorkflowMapTargetPathWriter.Parse(rule.Target);
                if (parsedTarget.ContainsWildcard)
                {
                    diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint}.target cannot use wildcard writes in this release.", $"{locationHint}.target"));
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' has an invalid map target at {locationHint}.target: {ex.Message}", $"{locationHint}.target"));
            }
        }

        if (rule.Lookup is not null)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint}.lookup is not supported in this release.", $"{locationHint}.lookup"));
        }

        if (rule.Validate is not null)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint}.validate is not supported in this release.", $"{locationHint}.validate"));
        }

        if (rule.From is not null)
        {
            if (string.IsNullOrWhiteSpace(rule.From))
            {
                diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint}.from must be a non-empty source expression when provided.", $"{locationHint}.from"));
            }
            else
            {
                ValidateJsonPathExpression(taskName, "map from", rule.From, $"{locationHint}.from", diagnostics);
            }
        }

        if (rule.Expression is not null)
        {
            if (string.IsNullOrWhiteSpace(rule.Expression))
            {
                diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint}.expression must be a non-empty expression when provided.", $"{locationHint}.expression"));
            }
            else
            {
                ValidateValueExpression(taskName, rule.Expression, $"{locationHint}.expression", diagnostics);
            }
        }

        if (rule.Foreach is not null)
        {
            if (string.IsNullOrWhiteSpace(rule.Foreach))
            {
                diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint}.foreach must be a non-empty source expression when provided.", $"{locationHint}.foreach"));
            }
            else
            {
                ValidateJsonPathExpression(taskName, "map foreach", rule.Foreach, $"{locationHint}.foreach", diagnostics);
            }
        }

        ValidateConditionExpression(taskName, "when", rule.When, $"{locationHint}.when", diagnostics);

        if (rule.Map is not null)
        {
            ValidateMapDefinition(taskName, rule.Map, $"{locationHint}.map", diagnostics);
        }

        var hasFrom = !string.IsNullOrWhiteSpace(rule.From);
        var hasExpression = !string.IsNullOrWhiteSpace(rule.Expression);
        var hasForeach = !string.IsNullOrWhiteSpace(rule.Foreach);
        var hasNestedMap = rule.Map is not null;
        var scalarSourceCount = (hasFrom ? 1 : 0) + (hasExpression ? 1 : 0) + (rule.HasConstant ? 1 : 0);
        var sourceKindCount = scalarSourceCount + (hasForeach ? 1 : 0) + (!hasForeach && hasNestedMap ? 1 : 0);

        if (hasForeach && !hasNestedMap)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint} must define a nested 'map' block when 'foreach' is present.", locationHint));
        }

        if (sourceKindCount != 1)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint} must define exactly one source kind using 'from', 'expression', 'constant', nested 'map', or 'foreach' with nested 'map'.", locationHint));
        }

        var isScalarSource = scalarSourceCount == 1 && !hasForeach && !hasNestedMap;
        if (rule.Transform.Count > 0 && !isScalarSource)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint}.transform is supported only for scalar-producing rules.", $"{locationHint}.transform"));
        }

        if (rule.HasDefault && !isScalarSource)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint}.default is supported only for scalar-producing rules.", $"{locationHint}.default"));
        }

        foreach (var transformName in rule.Transform)
        {
            if (!IsSupportedMapTransform(transformName))
            {
                diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint}.transform contains unsupported transform '{transformName}'.", $"{locationHint}.transform"));
            }
        }
    }

    private static void ValidateForkTask(
        string taskName,
        string locationHint,
        ForkTaskDefinition forkTask,
        ICollection<WorkflowCompilationDiagnostic> diagnostics,
        IReadOnlySet<string> extensionNames,
        IReadOnlySet<string> globalExtensionNames)
    {
        var forkLocationHint = $"{locationHint}.fork";

        if (forkTask.Branches.Count == 0)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {forkLocationHint}.branches must define at least one branch.", $"{forkLocationHint}.branches"));
            return;
        }

        var duplicateBranchNames = forkTask.Branches
            .GroupBy(branch => branch.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        foreach (var branchName in duplicateBranchNames)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {forkLocationHint}.branches defines duplicate branch name '{branchName}'.", $"{forkLocationHint}.branches"));
        }

        foreach (var branch in forkTask.Branches)
        {
            if (branch.Do.Count == 0)
            {
                diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {forkLocationHint}.branches['{branch.Name}'].do must define non-empty tasks.", $"{forkLocationHint}.branches['{branch.Name}'].do"));
                continue;
            }

            ValidateNestedTasks(branch.Do, $"{forkLocationHint}.branches['{branch.Name}']", extensionNames, globalExtensionNames, diagnostics);
        }

        if (!string.IsNullOrWhiteSpace(forkTask.Join?.Mode) && !string.Equals(forkTask.Join.Mode, "all", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {forkLocationHint}.join.mode supports only 'all' in this release.", $"{forkLocationHint}.join.mode"));
        }

        ValidateConditionExpression(taskName, "if", forkTask.If, $"{locationHint}.if", diagnostics);
        ValidateConditionExpression(taskName, "filter", forkTask.Filter, $"{locationHint}.filter", diagnostics);
    }

    private static void ValidateWaitTask(
        string taskName,
        string locationHint,
        WaitTaskDefinition waitTask,
        ICollection<WorkflowCompilationDiagnostic> diagnostics)
    {
        var forLocationHint = $"{locationHint}.wait.for";
        var untilLocationHint = $"{locationHint}.wait.until";
        var hasFor = !string.IsNullOrWhiteSpace(waitTask.For);
        var hasUntil = !string.IsNullOrWhiteSpace(waitTask.Until);

        if (hasFor == hasUntil)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint}.wait must define exactly one of 'for' or 'until'.", $"{locationHint}.wait"));
            return;
        }

        if (hasFor)
        {
            try
            {
                var duration = XmlConvert.ToTimeSpan(waitTask.For!);
                if (duration <= TimeSpan.Zero)
                {
                    diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {forLocationHint} must be greater than zero.", forLocationHint));
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' has an invalid wait duration at {forLocationHint}: {ex.Message}", forLocationHint));
            }
        }

        if (hasUntil && !DateTimeOffset.TryParse(waitTask.Until, out _))
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' has an invalid wait timestamp at {untilLocationHint}.", untilLocationHint));
        }

        ValidateConditionExpression(taskName, "if", waitTask.If, $"{locationHint}.if", diagnostics);
        ValidateConditionExpression(taskName, "filter", waitTask.Filter, $"{locationHint}.filter", diagnostics);
        ValidateSingleNamedThenTarget(taskName, locationHint, "wait", waitTask.Then, diagnostics);
    }

    private static void ValidateDoTask(
        string taskName,
        string locationHint,
        DoTaskDefinition doTask,
        ICollection<WorkflowCompilationDiagnostic> diagnostics,
        IReadOnlySet<string> extensionNames,
        IReadOnlySet<string> globalExtensionNames)
    {
        var ifLocationHint = $"{locationHint}.if";
        var filterLocationHint = $"{locationHint}.filter";
        var whileLocationHint = $"{locationHint}.while";
        var hasFor = doTask.ForIn is not null
            || doTask.ForEach is not null
            || doTask.ForAt is not null
            || doTask.ForSize is not null
            || doTask.ForMaxConcurrency is not null
            || doTask.ForInput is not null
            || doTask.ForCollect is not null
            || doTask.ForCollectInclude is not null
            || doTask.ForOnError is not null;

        if (!string.IsNullOrWhiteSpace(doTask.If) && !string.IsNullOrWhiteSpace(doTask.Filter))
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint} defines both 'if' and 'filter'. Do tasks support only one pre-condition field.", locationHint));
        }

        ValidateConditionExpression(taskName, "if", doTask.If, ifLocationHint, diagnostics);
        ValidateConditionExpression(taskName, "filter", doTask.Filter, filterLocationHint, diagnostics);

        if (doTask.While is not null && hasFor)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {whileLocationHint} cannot combine 'while' with 'for'. Use exactly one loop mode for do tasks.", whileLocationHint));
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint}.for cannot combine 'for' with 'while'. Use exactly one loop mode for do tasks.", $"{locationHint}.for"));
        }

        if (hasFor)
        {
            var forLocationHint = $"{locationHint}.for";

            if (string.IsNullOrWhiteSpace(doTask.ForIn))
            {
                diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {forLocationHint}.in must define a non-empty source expression when 'for' is present.", $"{forLocationHint}.in"));
            }

            if (string.IsNullOrWhiteSpace(doTask.ForEach))
            {
                diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {forLocationHint}.each must define a non-empty item variable when 'for' is present.", $"{forLocationHint}.each"));
            }

            if (doTask.ForAt is not null && string.IsNullOrWhiteSpace(doTask.ForAt))
            {
                diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {forLocationHint}.at must be a non-empty index variable when provided.", $"{forLocationHint}.at"));
            }

            if (doTask.ForSize is <= 0)
            {
                diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {forLocationHint}.size must be a positive integer when provided.", $"{forLocationHint}.size"));
            }

            if (doTask.ForMaxConcurrency is <= 0)
            {
                diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {forLocationHint}.maxConcurrency must be a positive integer when provided.", $"{forLocationHint}.maxConcurrency"));
            }

            if (doTask.ForInput is not null && doTask.ForMaxConcurrency is null)
            {
                diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {forLocationHint}.input is supported only when maxConcurrency is provided.", $"{forLocationHint}.input"));
            }

            if (doTask.ForCollectInclude is not null)
            {
                if (doTask.ForMaxConcurrency is null)
                {
                    diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {forLocationHint}.collect.include is supported only when maxConcurrency is provided.", $"{forLocationHint}.collect.include"));
                }

                ValidateCollectInclude(taskName, forLocationHint, doTask.ForCollectInclude.ToArray(), diagnostics);
            }

            if (doTask.ForMaxConcurrency is not null)
            {
                if (string.IsNullOrWhiteSpace(doTask.ForCollect))
                {
                    diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {forLocationHint}.collect must define a non-empty result variable when maxConcurrency is provided.", $"{forLocationHint}.collect"));
                }

                if (doTask.Do.Count == 0)
                {
                    diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint}.do must define at least one nested task when for.maxConcurrency is provided.", $"{locationHint}.do"));
                }

                foreach (var suspendingTask in EnumerateSuspendingTasks(doTask.Do))
                {
                    diagnostics.Add(new WorkflowCompilationDiagnostic(
                        $"Task '{taskName}' at {forLocationHint} cannot use suspendable task '{suspendingTask.TaskName}' inside a parallel for body.",
                        suspendingTask.LocationHint));
                }
            }

            if (!string.IsNullOrWhiteSpace(doTask.ForOnError)
                && !string.Equals(doTask.ForOnError, "failFast", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(doTask.ForOnError, "collect", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {forLocationHint}.onError must be either 'failFast' or 'collect'.", $"{forLocationHint}.onError"));
            }
        }

        ValidateGuard(taskName, locationHint, doTask.Guard, diagnostics);

        if (doTask.Guard is not null && doTask.While is null && !hasFor)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint}.guard is unsupported unless the do task defines either 'while' or 'for'.", $"{locationHint}.guard"));
        }

        if (doTask.While is null)
        {
            ValidateNestedTasks(doTask.Do, locationHint, extensionNames, globalExtensionNames, diagnostics);
            return;
        }

        if (string.IsNullOrWhiteSpace(doTask.While))
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {whileLocationHint} must define a non-empty while expression when 'while' is present.", whileLocationHint));
            return;
        }

        ValidateConditionExpression(taskName, "while", doTask.While, whileLocationHint, diagnostics);
        ValidateNestedTasks(doTask.Do, locationHint, extensionNames, globalExtensionNames, diagnostics);
    }

    private static void ValidateCollectInclude(
        string taskName,
        string forLocationHint,
        IReadOnlyList<string> include,
        ICollection<WorkflowCompilationDiagnostic> diagnostics)
    {
        var allowed = new HashSet<string>(["index", "success", "item", "output", "error"], StringComparer.OrdinalIgnoreCase);
        if (include.Count == 0)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {forLocationHint}.collect.include must contain at least one result field.", $"{forLocationHint}.collect.include"));
            return;
        }

        foreach (var field in include)
        {
            if (!allowed.Contains(field))
            {
                diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {forLocationHint}.collect.include contains unsupported field '{field}'.", $"{forLocationHint}.collect.include"));
            }
        }

        if (include.Distinct(StringComparer.OrdinalIgnoreCase).Count() != include.Count)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {forLocationHint}.collect.include cannot contain duplicate fields.", $"{forLocationHint}.collect.include"));
        }
    }

    private static void ValidateNestedTasks(
        IDictionary<string, TaskDefinition> tasks,
        string parentLocationHint,
        IReadOnlySet<string> extensionNames,
        IReadOnlySet<string> globalExtensionNames,
        ICollection<WorkflowCompilationDiagnostic> diagnostics)
    {
        var taskNames = tasks.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            ValidateTask(task.Key, task.Value, taskNames, extensionNames, globalExtensionNames, diagnostics, $"{parentLocationHint}");
        }
    }

    private static IEnumerable<(string TaskName, string LocationHint)> EnumerateSuspendingTasks(IDictionary<string, TaskDefinition> tasks, string parentLocationHint = "")
    {
        foreach (var (taskName, definition) in tasks)
        {
            var locationHint = string.IsNullOrWhiteSpace(parentLocationHint)
                ? BuildLocationHint(taskName)
                : $"{parentLocationHint}.do['{taskName}']";

            if (definition is ListenTaskDefinition or WaitTaskDefinition)
            {
                yield return (taskName, locationHint);
            }

            switch (definition)
            {
                case DoTaskDefinition doTask:
                    foreach (var nested in EnumerateSuspendingTasks(doTask.Do, locationHint))
                    {
                        yield return nested;
                    }

                    break;
                case ForkTaskDefinition forkTask:
                    foreach (var branch in forkTask.Branches)
                    {
                        foreach (var nested in EnumerateSuspendingTasks(branch.Do, $"{locationHint}.fork.branches['{branch.Name}']"))
                        {
                            yield return nested;
                        }
                    }

                    break;
                case TryTaskDefinition tryTask:
                    foreach (var nested in EnumerateSuspendingTasks(tryTask.Try, $"{locationHint}.try"))
                    {
                        yield return nested;
                    }

                    foreach (var catchDefinition in tryTask.Catch)
                    {
                        foreach (var nested in EnumerateSuspendingTasks(catchDefinition.Do, $"{locationHint}.catch"))
                        {
                            yield return nested;
                        }
                    }

                    if (tryTask.Finally is not null)
                    {
                        foreach (var nested in EnumerateSuspendingTasks(tryTask.Finally, $"{locationHint}.finally"))
                        {
                            yield return nested;
                        }
                    }

                    break;
                case RunTaskDefinition { Inline: not null } runTask:
                    foreach (var nested in EnumerateSuspendingTasks(runTask.Inline.Do, $"{locationHint}.run.workflow"))
                    {
                        yield return nested;
                    }

                    break;
            }
        }
    }

    private static void ValidateListenTask(
        string taskName,
        string locationHint,
        ListenTaskDefinition listenTask,
        ICollection<WorkflowCompilationDiagnostic> diagnostics)
    {
        ValidateConditionExpression(taskName, "if", listenTask.If, $"{locationHint}.if", diagnostics);
        ValidateConditionExpression(taskName, "filter", listenTask.Filter, $"{locationHint}.filter", diagnostics);
        ValidateSingleNamedThenTarget(taskName, locationHint, "listen", listenTask.Then, diagnostics);
    }

    private static void ValidateTryTask(
        string taskName,
        string locationHint,
        TryTaskDefinition tryTask,
        ICollection<WorkflowCompilationDiagnostic> diagnostics,
        IReadOnlySet<string> extensionNames,
        IReadOnlySet<string> globalExtensionNames)
    {
        ValidateConditionExpression(taskName, "if", tryTask.If, $"{locationHint}.if", diagnostics);
        ValidateConditionExpression(taskName, "filter", tryTask.Filter, $"{locationHint}.filter", diagnostics);

        if (tryTask.Try.Count == 0)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint} must define at least one nested task in 'try'.", $"{locationHint}.try"));
        }
        else
        {
            ValidateNestedTasks(tryTask.Try, $"{locationHint}.try", extensionNames, globalExtensionNames, diagnostics);
        }

        for (var catchIndex = 0; catchIndex < tryTask.Catch.Count; catchIndex++)
        {
            var catchDefinition = tryTask.Catch[catchIndex];
            var catchLocationHint = $"{locationHint}.catch[{catchIndex}]";

            if (catchDefinition.Do.Count == 0)
            {
                diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {catchLocationHint}.do must define non-empty tasks.", $"{catchLocationHint}.do"));
            }
            else
            {
                ValidateNestedTasks(catchDefinition.Do, catchLocationHint, extensionNames, globalExtensionNames, diagnostics);
            }

            ValidateConditionExpression(taskName, "catch.when", catchDefinition.When, $"{catchLocationHint}.when", diagnostics);
        }

        if (tryTask.Finally is null)
        {
            return;
        }

        if (tryTask.Finally.Count == 0)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint}.finally must define non-empty tasks when present.", $"{locationHint}.finally"));
            return;
        }

        ValidateNestedTasks(tryTask.Finally, $"{locationHint}.finally", extensionNames, globalExtensionNames, diagnostics);
    }

    private static void ValidateConditionExpression(
        string taskName,
        string fieldName,
        string? expression,
        string locationHint,
        ICollection<WorkflowCompilationDiagnostic> diagnostics)
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
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' has an invalid {fieldName} expression at {locationHint}: {ex.Message}", locationHint));
        }
    }

    private static void ValidateJsonPathExpression(
        string taskName,
        string fieldName,
        string expression,
        string locationHint,
        ICollection<WorkflowCompilationDiagnostic> diagnostics)
    {
        try
        {
            _ = JsonPath.SelectTokens(new JsonObject(), expression).FirstOrDefault();
        }
        catch (Exception ex)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' has an invalid {fieldName} expression at {locationHint}: {ex.Message}", locationHint));
        }
    }

    private static void ValidateValueExpression(
        string taskName,
        string expression,
        string locationHint,
        ICollection<WorkflowCompilationDiagnostic> diagnostics)
    {
        try
        {
            WorkflowValueExpressionEvaluator.ValidateSyntax(expression, new JsonObject());
        }
        catch (Exception ex)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' has an invalid map expression at {locationHint}: {ex.Message}", locationHint));
        }
    }

    private static bool IsSupportedMapTransform(string transformName)
    {
        return transformName switch
        {
            "trim" => true,
            "lower" => true,
            "upper" => true,
            "toNumber" => true,
            "toString" => true,
            "toDate" => true,
            _ => false
        };
    }

    private static void ValidateGuard(
        string taskName,
        string locationHint,
        LoopGuardPolicyDefinition? guard,
        ICollection<WorkflowCompilationDiagnostic> diagnostics)
    {
        if (guard is null)
        {
            return;
        }

        if (guard.MaxIterations is <= 0)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint}.guard.maxIterations must be greater than zero when provided.", $"{locationHint}.guard.maxIterations"));
        }

        if (guard.MaxRepeatedPayloads is <= 0)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint}.guard.maxRepeatedPayloads must be greater than zero when provided.", $"{locationHint}.guard.maxRepeatedPayloads"));
        }

        if (guard.TimeoutMs is <= 0)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint}.guard.timeoutMs must be greater than zero when provided.", $"{locationHint}.guard.timeoutMs"));
        }

        if (!string.IsNullOrWhiteSpace(guard.OnCancel)
            && !string.Equals(guard.OnCancel.Trim(), "fail", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(guard.OnCancel.Trim(), "exit", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint}.guard.onCancel has unsupported value '{guard.OnCancel}'. Supported values are 'fail' and 'exit'.", $"{locationHint}.guard.onCancel"));
        }
    }

    private static void ValidateUnreachableTasks(WorkflowDefinition definition, ICollection<WorkflowCompilationDiagnostic> diagnostics)
    {
        var graph = new WorkflowTaskGraphBuilder().Build(definition);
        if (string.IsNullOrWhiteSpace(graph.StartCandidateTaskName) || !graph.Nodes.ContainsKey(graph.StartCandidateTaskName))
        {
            return;
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(graph.StartCandidateTaskName);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current) || !graph.Adjacency.TryGetValue(current, out var nextTasks))
            {
                continue;
            }

            foreach (var next in nextTasks)
            {
                queue.Enqueue(next);
            }
        }

        foreach (var rootTask in graph.RootTaskNames)
        {
            if (!visited.Contains(rootTask))
            {
                diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{rootTask}' is unreachable from start task '{graph.StartCandidateTaskName}'.", BuildLocationHint(rootTask)));
            }
        }
    }

    private static List<string> CollectThenTargets(object? thenValue, string taskName, string locationHint, ICollection<WorkflowCompilationDiagnostic> diagnostics)
    {
        var normalizer = new ThenTargetNormalizer((parentName, displayName) => $"{parentName}.__validation_{displayName}");

        try
        {
            return normalizer.Normalize(thenValue, taskName, locationHint)
                .OfType<NamedTaskTarget>()
                .Select(target => target.Name)
                .ToList();
        }
        catch (InvalidOperationException ex)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic(ex.Message, locationHint));
            return [];
        }
    }

    private static string? TryGetSingleNamedTarget(object? thenValue, string taskName, string locationHint, ICollection<WorkflowCompilationDiagnostic> diagnostics)
    {
        var normalizer = new ThenTargetNormalizer((parentName, displayName) => $"{parentName}.__validation_{displayName}");

        try
        {
            var normalizedTargets = normalizer.Normalize(thenValue, taskName, locationHint).ToList();
            return normalizedTargets.Count == 1 && normalizedTargets[0] is NamedTaskTarget target ? target.Name : null;
        }
        catch (InvalidOperationException ex)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic(ex.Message, locationHint));
            return null;
        }
    }

    private static void ValidateSingleNamedThenTarget(
        string taskName,
        string locationHint,
        string taskKind,
        object? thenValue,
        ICollection<WorkflowCompilationDiagnostic> diagnostics)
    {
        var normalizer = new ThenTargetNormalizer((parentName, displayName) => $"{parentName}.__validation_{displayName}");

        try
        {
            var normalizedTargets = normalizer.Normalize(thenValue, taskName, locationHint).ToList();
            if (normalizedTargets.Count > 1 || normalizedTargets.Any(static target => target is not NamedTaskTarget))
            {
                diagnostics.Add(new WorkflowCompilationDiagnostic($"Task '{taskName}' at {locationHint} uses an unsupported {taskKind} pattern. {char.ToUpperInvariant(taskKind[0]) + taskKind[1..]} tasks support at most one named 'then' target.", locationHint));
            }
        }
        catch (InvalidOperationException ex)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic(ex.Message, locationHint));
        }
    }

    private static string BuildLocationHint(string taskName) => $"document.do['{taskName}']";

    private static string BuildExtensionLocationHint(string extensionName) => $"document.use.extensions['{extensionName}']";

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

    private static WorkflowSemanticValidationResult Validate(
        WorkflowDefinition definition,
        IReadOnlyList<string> taskOrder,
        ICollection<WorkflowCompilationDiagnostic> diagnostics)
    {
        if (definition.Do.Count == 0)
        {
            diagnostics.Add(new WorkflowCompilationDiagnostic("Workflow definition does not contain any tasks.", "document"));
            return new WorkflowSemanticValidationResult(null, diagnostics.ToArray());
        }

        var taskNames = definition.Do.Keys.ToHashSet(StringComparer.Ordinal);
        var extensionNames = ValidateExtensions(definition, diagnostics);
        var globalExtensionNames = definition.Use?.Extensions
            .Where(extension => string.Equals(extension.Extend, "all", StringComparison.OrdinalIgnoreCase))
            .Select(extension => extension.Name)
            .ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);

        foreach (var task in definition.Do)
        {
            ValidateTask(task.Key, task.Value, taskNames, extensionNames, globalExtensionNames, diagnostics);
        }

        ValidateUnreachableTasks(definition, diagnostics);

        if (diagnostics.Count > 0)
        {
            return new WorkflowSemanticValidationResult(null, diagnostics.ToArray());
        }

        var boundExtensions = definition.Use?.Extensions.Select(extension => new BoundWorkflowExtension(
            extension.Name,
            string.Equals(extension.Extend, "all", StringComparison.OrdinalIgnoreCase),
            extension.Before,
            extension.BeforeWhen,
            extension.After,
            extension.AfterWhen,
            extension.OnError,
            extension.OnErrorWhen)).ToArray() ?? [];

        return new WorkflowSemanticValidationResult(new BoundWorkflow(
            definition.Metadata,
            definition.Do.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            taskOrder,
            boundExtensions), diagnostics.ToArray());
    }
}
