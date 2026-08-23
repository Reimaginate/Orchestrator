namespace Reimaginate.Orchestrator.DSL;

public sealed class WorkflowDefinition
{
    public WorkflowMetadataDefinition? Metadata { get; init; }

    public WorkflowUseDefinition? Use { get; init; }

    public IDictionary<string, TaskDefinition> Do { get; init; } = new Dictionary<string, TaskDefinition>(StringComparer.Ordinal);
}

public sealed class WorkflowMetadataDefinition
{
    public string? Dsl { get; set; }

    public string? Namespace { get; set; }

    public string? Name { get; set; }

    public string? Version { get; set; }
}

public sealed class WorkflowUseDefinition
{
    public IList<WorkflowExtensionDefinition> Extensions { get; init; } = new List<WorkflowExtensionDefinition>();
}

public sealed class WorkflowExtensionDefinition
{
    public string Name { get; set; } = string.Empty;

    public string? Extend { get; set; }

    public WorkflowTaskBlockDefinition? Before { get; set; }

    public string? BeforeWhen { get; set; }

    public WorkflowTaskBlockDefinition? After { get; set; }

    public string? AfterWhen { get; set; }

    public WorkflowTaskBlockDefinition? OnError { get; set; }

    public string? OnErrorWhen { get; set; }
}

public sealed class WorkflowTaskBlockDefinition
{
    public IDictionary<string, TaskDefinition> Do { get; init; } = new Dictionary<string, TaskDefinition>(StringComparer.Ordinal);

    public IList<string> TaskOrder { get; init; } = new List<string>();
}

public abstract class TaskDefinition
{
    public object? Then { get; set; }

    public string? If { get; set; }

    public string? Filter { get; set; }

    public IList<string> Extend { get; init; } = new List<string>();
}

public sealed class CallTaskDefinition : TaskDefinition
{
    public string Call { get; set; } = string.Empty;

    public IDictionary<string, object>? With { get; set; }

    public TaskOutputDefinition? Output { get; set; }
}

public sealed class RunTaskDefinition : TaskDefinition
{
    public RunWorkflowDefinition Workflow { get; set; } = new();

    public TaskOutputDefinition? Output { get; set; }

    public WorkflowTaskBlockDefinition? Inline { get; set; }
}

public sealed class RunWorkflowDefinition
{
    public string WorkflowType { get; set; } = string.Empty;

    public IDictionary<string, object>? Input { get; set; }
}

public sealed class ListenTaskDefinition : TaskDefinition
{
    public IList<ListenTargetDefinition> ToAny { get; init; } = new List<ListenTargetDefinition>();

    public string? Read { get; set; }

    public TaskOutputDefinition? Output { get; set; }
}

public sealed class ListenTargetDefinition
{
    public string? Type { get; set; }

    public string? Filter { get; set; }
}

public sealed class WaitTaskDefinition : TaskDefinition
{
    public string? For { get; set; }

    public string? Until { get; set; }

    public TaskOutputDefinition? Output { get; set; }
}

public sealed class TerminateTaskDefinition : TaskDefinition
{
    public string? Status { get; set; }

    public object? Output { get; set; }

    public string? Reason { get; set; }
}

public sealed class RaiseTaskDefinition : TaskDefinition
{
    public string? ErrorType { get; set; }

    public string? Message { get; set; }

    public IDictionary<string, object>? Data { get; set; }
}

public sealed class EmitTaskDefinition : TaskDefinition
{
    public string? EventType { get; set; }

    public string? Source { get; set; }

    public string? Subject { get; set; }

    public string? Id { get; set; }

    public string? To { get; set; }

    public IDictionary<string, object>? Data { get; set; }

    public TaskOutputDefinition? Output { get; set; }
}

public sealed class MapTaskDefinition : TaskDefinition
{
    public MapDefinition Map { get; set; } = new();

    public TaskOutputDefinition? Output { get; set; }
}

public sealed class MapDefinition
{
    public string? Input { get; set; }

    public IDictionary<string, object>? Schema { get; set; }

    public IDictionary<string, object>? Options { get; set; }

    public IList<MapRuleDefinition> Rules { get; init; } = new List<MapRuleDefinition>();
}

public sealed class MapRuleDefinition
{
    public string Target { get; set; } = string.Empty;

    public string? From { get; set; }

    public string? Expression { get; set; }

    public object? Constant { get; set; }

    public bool HasConstant { get; set; }

    public string? When { get; set; }

    public IList<string> Transform { get; init; } = new List<string>();

    public object? Default { get; set; }

    public bool HasDefault { get; set; }

    public string? Foreach { get; set; }

    public MapDefinition? Map { get; set; }

    public MapLookupDefinition? Lookup { get; set; }

    public MapValidationDefinition? Validate { get; set; }
}

public sealed class MapLookupDefinition
{
    public string? From { get; set; }

    public string? Using { get; set; }
}

public sealed class MapValidationDefinition
{
    public bool? Required { get; set; }

    public string? Pattern { get; set; }
}

public sealed class StashTaskDefinition : TaskDefinition
{
    public IDictionary<string, object>? Values { get; set; }
}


public sealed class ForkTaskDefinition : TaskDefinition
{
    public IList<ForkBranchDefinition> Branches { get; init; } = new List<ForkBranchDefinition>();

    public ForkJoinDefinition? Join { get; set; }

    public TaskOutputDefinition? Output { get; set; }
}

public sealed class ForkBranchDefinition
{
    public string Name { get; set; } = string.Empty;

    public IDictionary<string, TaskDefinition> Do { get; init; } = new Dictionary<string, TaskDefinition>(StringComparer.Ordinal);
}

public sealed class ForkJoinDefinition
{
    public string? Mode { get; set; }
}

public sealed class DoTaskDefinition : TaskDefinition
{
    public IDictionary<string, TaskDefinition> Do { get; init; } = new Dictionary<string, TaskDefinition>(StringComparer.Ordinal);

    public IDictionary<string, object>? With { get; set; }

    public string? ForIn { get; set; }

    public string? ForEach { get; set; }

    public string? ForAt { get; set; }

    public int? ForSize { get; set; }

    public int? ForMaxConcurrency { get; set; }

    public IDictionary<string, object>? ForInput { get; set; }

    public string? ForCollect { get; set; }

    public IList<string>? ForCollectInclude { get; set; }

    public string? ForOnError { get; set; }

    public string? While { get; set; }

    public LoopGuardPolicyDefinition? Guard { get; set; }

    public TaskOutputDefinition? Output { get; set; }
}

public sealed class LoopGuardPolicyDefinition
{
    public int? MaxIterations { get; set; }

    public int? MaxRepeatedPayloads { get; set; }

    public int? TimeoutMs { get; set; }

    public string? OnCancel { get; set; }
}


public sealed class TryTaskDefinition : TaskDefinition
{
    public IDictionary<string, TaskDefinition> Try { get; init; } = new Dictionary<string, TaskDefinition>(StringComparer.Ordinal);

    public IList<CatchDefinition> Catch { get; init; } = new List<CatchDefinition>();

    public IDictionary<string, TaskDefinition>? Finally { get; set; }

    public TaskOutputDefinition? Output { get; set; }
}

public sealed class CatchDefinition
{
    public string? Errors { get; set; }

    public string? When { get; set; }

    public IDictionary<string, TaskDefinition> Do { get; init; } = new Dictionary<string, TaskDefinition>(StringComparer.Ordinal);

    public TaskOutputDefinition? Output { get; set; }

    public object? Then { get; set; }
}

public sealed class SwitchTaskDefinition : TaskDefinition
{
    public IList<KeyValuePair<string, SwitchBranchDefinition>> Switch { get; init; } = new List<KeyValuePair<string, SwitchBranchDefinition>>();
}

public sealed class SwitchBranchDefinition
{
    public string? When { get; set; }

    public TaskOutputDefinition? Output { get; set; }

    public object? Then { get; set; }
}

public sealed class TaskOutputDefinition
{
    public object? As { get; set; }

    public IDictionary<string, object>? Stash { get; set; }
}
