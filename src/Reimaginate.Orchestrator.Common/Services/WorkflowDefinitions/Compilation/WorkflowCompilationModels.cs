using System.Text.Json.Nodes;
using Reimaginate.Orchestrator.Common.Executors;
using Reimaginate.Orchestrator.Common.Helpers;
using Reimaginate.Orchestrator.DSL;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowDefinitions.Compilation;

internal enum WorkflowPlanNodeKind
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

internal enum WorkflowDoLoopMode
{
    While,
    ForEach
}

internal enum WorkflowCompilationConstraintKind
{
    SwitchBranchRequiresSingleNamedTarget,
    ListenSupportsSingleNamedTarget,
    TaskDoesNotSupportFanOut,
    SwitchSupportsSingleDefault,
    DoWhileRequiresBody,
    DoWhileRequiresExpression,
    DoWhileInvalidExpression,
    DoTaskConflictingConditions
}

internal sealed record WorkflowCompilationDiagnostic(string Message, string LocationHint);

internal sealed record BoundWorkflowExtension(
    string Name,
    bool ExtendAll,
    WorkflowTaskBlockDefinition? Before,
    string? BeforeWhen,
    WorkflowTaskBlockDefinition? After,
    string? AfterWhen,
    WorkflowTaskBlockDefinition? OnError,
    string? OnErrorWhen,
    WorkflowCompilationPlan? BeforePlan = null,
    WorkflowCompilationPlan? AfterPlan = null,
    WorkflowCompilationPlan? OnErrorPlan = null);

internal sealed record BoundWorkflow(
    WorkflowMetadataDefinition? Metadata,
    IReadOnlyDictionary<string, TaskDefinition> Tasks,
    IReadOnlyList<string> TaskOrder,
    IReadOnlyList<BoundWorkflowExtension> Extensions);

internal sealed record WorkflowSemanticValidationResult(
    BoundWorkflow? Workflow,
    IReadOnlyList<WorkflowCompilationDiagnostic> Diagnostics)
{
    public bool IsSuccessful => Workflow is not null && Diagnostics.Count == 0;
}

internal sealed record WorkflowPlanBranch(
    string ParentTaskName,
    string? When,
    string? TargetTaskName,
    string RouteTaskName,
    bool IsDefault,
    string LocationHint,
    JsonObject? OutputMap,
    JsonObject? OutputStashMap);

internal sealed record WorkflowPlanTransition(
    string FromTaskName,
    string ToTaskName,
    string LocationHint,
    string? RouteTaskName = null);

internal sealed record WorkflowPlanConstraint(
    WorkflowCompilationConstraintKind Kind,
    string TaskName,
    string LocationHint,
    string Description);

internal sealed record WorkflowLoopGuardPolicy(
    int? MaxIterations,
    int? MaxRepeatedPayloads,
    TimeSpan? Timeout,
    LoopGuardCancelMode? CancelMode);

internal sealed record WorkflowPlanNode(
    string Name,
    WorkflowPlanNodeKind Kind,
    string LocationHint,
    TaskDefinition? Definition,
    IReadOnlyList<string> NextTaskNames,
    bool IsUserTask,
    IReadOnlyList<string> AppliedExtensions,
    string? SourceWorkflowType,
    string? ParentRunTaskName,
    JsonObject? InputMap,
    JsonObject? OutputMap,
    JsonObject? OutputStashMap,
    string? Condition,
    string? CallTarget,
    string? TerminalStatus = null,
    JsonNode? TerminalOutput = null,
    string? TerminalReason = null,
    WorkflowDoLoopMode? DoLoopMode = null,
    string? LoopSourceExpression = null,
    string? LoopItemVariable = null,
    string? LoopIndexVariable = null,
    int? LoopBatchSize = null,
    int? LoopMaxConcurrency = null,
    JsonObject? LoopInputMap = null,
    string? LoopCollectVariable = null,
    IReadOnlyList<string>? LoopCollectInclude = null,
    string? LoopErrorMode = null,
    WorkflowLoopGuardPolicy? LoopGuardPolicy = null,
    bool CaptureErrors = false,
    string? ConsoleStepName = null,
    WorkflowMapPlan? MapPlan = null);

internal sealed record WorkflowCompilationPlan(
    WorkflowMetadataDefinition? Metadata,
    IReadOnlyDictionary<string, WorkflowPlanNode> Nodes,
    IReadOnlyList<string> RootTaskNames,
    string StartTaskName,
    IReadOnlyList<WorkflowPlanTransition> Transitions,
    IReadOnlyDictionary<string, IReadOnlyList<WorkflowPlanBranch>> BranchesByTask,
    IReadOnlyList<WorkflowPlanConstraint> Constraints,
    IReadOnlyDictionary<string, BoundWorkflowExtension> Extensions);

internal sealed record WorkflowCompilationResult(
    WorkflowCompilationPlan? Plan,
    IReadOnlyList<WorkflowCompilationDiagnostic> Diagnostics)
{
    public bool IsSuccessful => Plan is not null && Diagnostics.Count == 0;
}
