namespace Reimaginate.Orchestrator.DSL;

public sealed record WorkflowAst(
    IReadOnlyList<TaskAst> Tasks,
    WorkflowMetadataAst? Metadata = null,
    WorkflowUseAst? Use = null);

public sealed record WorkflowMetadataAst(
    string? Dsl,
    string? Namespace,
    string? Name,
    string? Version);

public sealed record WorkflowUseAst(
    IReadOnlyList<WorkflowExtensionAst> Extensions);

public sealed record WorkflowExtensionAst(
    string Name,
    string? Extend,
    IReadOnlyList<TaskAst>? Before,
    string? BeforeWhen,
    IReadOnlyList<TaskAst>? After,
    string? AfterWhen,
    IReadOnlyList<TaskAst>? OnError,
    string? OnErrorWhen);

public abstract record TaskAst(
    string Name,
    object? Then,
    string? If,
    string? Filter,
    IReadOnlyList<string>? Extend);

public sealed record CallAst(
    string Name,
    string Call,
    IReadOnlyDictionary<string, object>? With,
    TaskOutputAst? Output,
    object? Then,
    string? If,
    string? Filter,
    IReadOnlyList<string>? Extend) : TaskAst(Name, Then, If, Filter, Extend);

public sealed record RunAst(
    string Name,
    RunWorkflowAst Workflow,
    TaskOutputAst? Output,
    object? Then,
    string? If,
    string? Filter,
    IReadOnlyList<string>? Extend) : TaskAst(Name, Then, If, Filter, Extend);

public sealed record RunWorkflowAst(
    string WorkflowType,
    IReadOnlyDictionary<string, object>? Input);

public sealed record ListenAst(
    string Name,
    IReadOnlyList<ListenTargetAst> ToAny,
    string? Read,
    TaskOutputAst? Output,
    object? Then,
    string? If,
    string? Filter,
    IReadOnlyList<string>? Extend) : TaskAst(Name, Then, If, Filter, Extend);

public sealed record ListenTargetAst(string? Type, string? Filter);

public sealed record WaitAst(
    string Name,
    string? For,
    string? Until,
    TaskOutputAst? Output,
    object? Then,
    string? If,
    string? Filter,
    IReadOnlyList<string>? Extend) : TaskAst(Name, Then, If, Filter, Extend);

public sealed record TerminateAst(
    string Name,
    string? Status,
    object? Output,
    string? Reason,
    object? Then,
    string? If,
    string? Filter,
    IReadOnlyList<string>? Extend) : TaskAst(Name, Then, If, Filter, Extend);

public sealed record RaiseAst(
    string Name,
    string? ErrorType,
    string? Message,
    IReadOnlyDictionary<string, object>? Data,
    object? Then,
    string? If,
    string? Filter,
    IReadOnlyList<string>? Extend) : TaskAst(Name, Then, If, Filter, Extend);


public sealed record EmitAst(
    string Name,
    string? EventType,
    string? Source,
    string? Subject,
    string? Id,
    string? To,
    IReadOnlyDictionary<string, object>? Data,
    TaskOutputAst? Output,
    object? Then,
    string? If,
    string? Filter,
    IReadOnlyList<string>? Extend) : TaskAst(Name, Then, If, Filter, Extend);

public sealed record MapAst(
    string Name,
    MapDefinitionAst Map,
    TaskOutputAst? Output,
    object? Then,
    string? If,
    string? Filter,
    IReadOnlyList<string>? Extend) : TaskAst(Name, Then, If, Filter, Extend);

public sealed record MapDefinitionAst(
    string? Input,
    IReadOnlyList<MapRuleAst> Rules,
    IReadOnlyDictionary<string, object>? Schema,
    IReadOnlyDictionary<string, object>? Options);

public sealed record MapRuleAst(
    string Target,
    string? From,
    string? Expression,
    object? Constant,
    bool HasConstant,
    string? When,
    IReadOnlyList<string>? Transform,
    object? Default,
    bool HasDefault,
    string? Foreach,
    MapDefinitionAst? Map,
    MapLookupAst? Lookup,
    MapValidationAst? Validate);

public sealed record MapLookupAst(
    string? From,
    string? Using);

public sealed record MapValidationAst(
    bool? Required,
    string? Pattern);

public sealed record StashAst(
    string Name,
    IReadOnlyDictionary<string, object>? Values,
    object? Then,
    string? If,
    string? Filter,
    IReadOnlyList<string>? Extend) : TaskAst(Name, Then, If, Filter, Extend);

public sealed record SwitchAst(
    string Name,
    IReadOnlyList<SwitchBranchAst> Branches,
    object? Then,
    string? If,
    string? Filter,
    IReadOnlyList<string>? Extend) : TaskAst(Name, Then, If, Filter, Extend);

public sealed record DoAst(
    string Name,
    IReadOnlyList<TaskAst> Tasks,
    IReadOnlyDictionary<string, object>? With,
    string? ForIn,
    string? ForEach,
    string? ForAt,
    int? ForSize,
    int? ForMaxConcurrency,
    IReadOnlyDictionary<string, object>? ForInput,
    string? ForCollect,
    IReadOnlyList<string>? ForCollectInclude,
    string? ForOnError,
    string? While,
    LoopGuardPolicyAst? Guard,
    TaskOutputAst? Output,
    object? Then,
    string? If,
    string? Filter,
    IReadOnlyList<string>? Extend) : TaskAst(Name, Then, If, Filter, Extend);

public sealed record ForkAst(
    string Name,
    IReadOnlyList<ForkBranchAst> Branches,
    string? JoinMode,
    TaskOutputAst? Output,
    object? Then,
    string? If,
    string? Filter,
    IReadOnlyList<string>? Extend) : TaskAst(Name, Then, If, Filter, Extend);

public sealed record ForkBranchAst(
    string Name,
    IReadOnlyList<TaskAst> Tasks);

public sealed record TryAst(
    string Name,
    IReadOnlyList<TaskAst> TryTasks,
    IReadOnlyList<CatchAst> Catches,
    IReadOnlyList<TaskAst>? FinallyTasks,
    TaskOutputAst? Output,
    object? Then,
    string? If,
    string? Filter,
    IReadOnlyList<string>? Extend) : TaskAst(Name, Then, If, Filter, Extend);

public sealed record CatchAst(
    string? Errors,
    string? When,
    IReadOnlyList<TaskAst> Tasks,
    TaskOutputAst? Output,
    object? Then);

public sealed record SwitchBranchAst(string Name, string? When, TaskOutputAst? Output, object? Then);

public sealed record TaskOutputAst(
    object? As,
    IReadOnlyDictionary<string, object>? Stash);

public sealed record LoopGuardPolicyAst(
    int? MaxIterations,
    int? MaxRepeatedPayloads,
    int? TimeoutMs,
    string? OnCancel);
