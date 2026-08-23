using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Reimaginate.Mediator;
using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.Common.Constants;
using Reimaginate.Orchestrator.Common.Diagnostics;
using Reimaginate.Orchestrator.Common.Helpers;
using Reimaginate.Orchestrator.Common.Requests.Internal.BuildAgentWorkflow;
using Reimaginate.Orchestrator.Common.Requests.Internal.ResolveAndLoadWorkflowDefinition;
using Reimaginate.Orchestrator.Common.Config;
using Reimaginate.Orchestrator.Common.Services.WorkflowExecution;
using Reimaginate.Orchestrator.Common.Services.WorkflowDefinitions;
using Reimaginate.Orchestrator.Common.Services.WorkflowInstancePersistence;
using Reimaginate.Orchestrator.Common.Services.WorkflowResume;
using Reimaginate.Orchestrator.Common.Services.Shutdown;
using Reimaginate.Orchestrator.Common.Models;
using Reimaginate.Orchestrator.Common.Stores.Checkpoints;
using Reimaginate.Orchestrator.DSL;

namespace Reimaginate.Orchestrator.Common.Requests.External.StartWorkflow;

public class StartWorkflowRequestHandler(IMediator mediator, ICheckpointStorage checkpointStorage, IWorkflowInstancePersistenceService workflowInstancePersistenceService, IWorkflowDefinitionService workflowDefinitionService, IWorkflowResumeRuntimeService workflowResumeRuntimeService, IWorkflowExecutionTraceSink workflowExecutionTraceSink, IWorkflowExecutionPolicyProvider workflowExecutionPolicyProvider, IOrchestratorShutdownSignal orchestratorShutdownSignal) : IHandler<StartWorkflowRequest, StartWorkflowResult>
{
    public const string ShutdownInterruptedFailureReason = "Workflow execution was interrupted by host shutdown.";

    public async Task<StartWorkflowResult> HandleAsync(StartWorkflowRequest request, CancellationToken cancellationToken)
    {
        var workflowInput = request.Input ?? new JsonObject();
        WorkflowStashHelper.SeedStashFromInput(workflowInput);
        workflowInput["__trigger"] = request.Input != null ? "event" : "manual";
        var initiatingEvent = workflowResumeRuntimeService.ResolveInitiatingEventContext(workflowInput);
        var workflowInstanceId = string.IsNullOrWhiteSpace(request.WorkflowInstanceId)
            ? Guid.NewGuid().ToString("N")
            : request.WorkflowInstanceId;
        var diagnosticsPath = workflowExecutionTraceSink.GetDiagnosticsPath(workflowInstanceId);
        var isReservedStart = !string.IsNullOrWhiteSpace(request.WorkflowInstanceId);

        #region Resolve and compile workflow definition
        // Resolve the workflow definition from storage and compile it into an executable workflow graph.

        var (definitionResponse, definitionError) = await mediator.TrySend(new ResolveAndLoadWorkflowDefinitionRequest
        {
            WorkflowType = request.WorkflowType
        }, cancellationToken);

        if (definitionResponse is null || definitionError is not null || !definitionResponse.Success)
        {
            await PersistReservedStartFailureAsync(
                isReservedStart,
                workflowInstanceId,
                request.WorkflowType,
                definitionResponse?.FailureReason ?? definitionError?.Message ?? "Error occurred while resolving workflow definition",
                null,
                initiatingEvent,
                request.ExecutionPolicyOverride,
                cancellationToken);

            return new StartWorkflowResult
            {
                Success = false,
                WorkflowInstanceId = workflowInstanceId,
                DiagnosticsPath = diagnosticsPath,
                Status = WorkflowInstanceStatuses.Failed,
                FailureReason = definitionResponse?.FailureReason ?? definitionError?.Message ?? "Error occurred while resolving workflow definition"
            };
        }

        // Build the executable agent workflow from the loaded AST and definition metadata.
        var (workflowResponse, ex) = await mediator.TrySend(new BuildAgentWorkflowRequest
        {
            WorkflowType = request.WorkflowType,
            Ast = definitionResponse.Ast,
            Definition = definitionResponse.Definition
        }, cancellationToken);

        if (workflowResponse is null || ex is not null || !workflowResponse.Success)
        {
            var failure = WorkflowExecutionFailureInfoBuilder.Build("Workflow failed before execution started.", ex);
            var failureReason = workflowResponse?.FailureReason ?? failure.FailureReason ?? ex?.Message ?? "Error occurred while processing request";
            var failureDetails = ex is not null ? failure.FailureDetails : null;

            await PersistReservedStartFailureAsync(
                isReservedStart,
                workflowInstanceId,
                request.WorkflowType,
                failureReason,
                failureDetails,
                initiatingEvent,
                request.ExecutionPolicyOverride,
                cancellationToken);

            return new StartWorkflowResult
            {
                Success = false,
                WorkflowInstanceId = workflowInstanceId,
                DiagnosticsPath = diagnosticsPath,
                Status = WorkflowInstanceStatuses.Failed,
                FailureReason = failureReason,
                FailureDetails = failureDetails
            };
        }

        #endregion

        #region Initialize workflow run and persist initial running state
        // Start an in-process execution stream and persist the initial running state before consuming events.

        var executionPolicy = workflowExecutionPolicyProvider.Resolve(request.WorkflowType)
            .Apply(request.ExecutionPolicyOverride);
        var checkpointMode = executionPolicy.Checkpoints;
        var durableStore = checkpointMode == WorkflowCheckpointingMode.Disabled
            ? null
            : checkpointStorage.CreateStore();
        var failureBufferedStore = checkpointMode == WorkflowCheckpointingMode.Failure && durableStore is not null
            ? new FailureBufferedCheckpointStore(durableStore)
            : null;
        var store = failureBufferedStore ?? durableStore;
        var checkpointManager = store is null ? null : CheckpointManager.CreateJson(store);
        var writeCheckpointsImmediately = checkpointMode == WorkflowCheckpointingMode.Enabled;
        string? currentCheckpointId = null;
        var checkpointRunId = workflowInstanceId;
        object? finalOutput = null;
        var status = WorkflowInstanceStatuses.Running;
        workflowResponse.DiagnosticsGraph["workflowType"] = request.WorkflowType;

        if (workflowExecutionTraceSink.IsEnabled)
        {
            await workflowExecutionTraceSink.WriteGraphAsync(request.WorkflowType, workflowResponse.DiagnosticsGraph, workflowInstanceId, cancellationToken);
            await workflowExecutionTraceSink.RecordAsync(workflowInstanceId, request.WorkflowType, "workflow.start_requested", new JsonObject
            {
                ["trigger"] = workflowInput["__trigger"]?.GetValue<string>(),
                ["originatingEventId"] = initiatingEvent.EventId,
                ["originatingEventType"] = initiatingEvent.EventType,
                ["originatingEventSource"] = initiatingEvent.EventSource,
                ["input"] = workflowInput.DeepClone()
            }, cancellationToken);
        }

        await workflowInstancePersistenceService.PersistWorkflowInstanceAsync(workflowInstanceId, request.WorkflowType, WorkflowInstanceStatuses.Running, null, null, null, currentCheckpointId,
            checkpointRunId, workflowDefinitionService.BuildAwaitingEventDescriptors(workflowResponse.Definition, currentCheckpointId), cancellationToken, initiatingEvent.EventId, initiatingEvent.EventType, initiatingEvent.EventSource,
            executionPolicyOverride: request.ExecutionPolicyOverride);

        #endregion

        #region Process workflow execution events
        // Track checkpoints and terminal signals while the workflow emits runtime events.

        var superStepStatus = WorkflowInstanceStatuses.Running;
        var sawWorkflowOutput = false;
        IReadOnlyCollection<string> runtimeActiveHaltingTaskNames = [];
        WorkflowTerminalResult? terminalResult = null;

        using var workflowExecutionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, orchestratorShutdownSignal.ShutdownToken);
        var executionCancellationToken = workflowExecutionCancellation.Token;

        try
        {
            executionCancellationToken.ThrowIfCancellationRequested();

            using (WorkflowExecutionTraceContext.BeginScope(workflowExecutionTraceSink, request.WorkflowType, workflowInstanceId))
            {
                await using var run = checkpointManager is null
                    ? await InProcessExecution.RunStreamingAsync(
                        workflowResponse.Workflow,
                        input: workflowInput,
                        cancellationToken: executionCancellationToken)
                    : await InProcessExecution.RunStreamingAsync(
                        workflowResponse.Workflow,
                        workflowInput,
                        checkpointManager,
                        workflowInstanceId,
                        executionCancellationToken);

                await foreach (var evt in run.WatchStreamAsync(executionCancellationToken))
                {
                    switch (evt)
                    {
                        case SuperStepStartedEvent:
                            superStepStatus = WorkflowInstanceStatuses.Running;
                            await workflowExecutionTraceSink.RecordAsync(workflowInstanceId, request.WorkflowType, "workflow.super_step_started", new JsonObject(), executionCancellationToken);
                            break;

                        case WorkflowErrorEvent workflowErrorEvent:
                            // Persist a failed status immediately when execution emits an error event.
                            superStepStatus = WorkflowInstanceStatuses.Failed;
                            var failure = WorkflowExecutionFailureInfoBuilder.Build("Workflow failed during start execution.", workflowErrorEvent.Exception);

                            await workflowExecutionTraceSink.RecordAsync(workflowInstanceId, request.WorkflowType, "workflow.error", failure.ToTracePayload(), executionCancellationToken);

                            await FlushFailureBufferedCheckpointsAsync(
                                failureBufferedStore,
                                durableStore,
                                workflowInstanceId,
                                request.WorkflowType,
                                executionCancellationToken);

                            await workflowInstancePersistenceService.PersistWorkflowInstanceAsync(workflowInstanceId, request.WorkflowType, superStepStatus, failure.FailureReason, failure.FailureDetails, DateTimeOffset.UtcNow, currentCheckpointId, checkpointRunId,
                                workflowDefinitionService.BuildAwaitingEventDescriptors(workflowResponse.Definition, currentCheckpointId), executionCancellationToken, initiatingEvent.EventId, initiatingEvent.EventType, initiatingEvent.EventSource,
                                executionPolicyOverride: request.ExecutionPolicyOverride);

                            return new StartWorkflowResult
                            {
                                Success = false,
                                WorkflowInstanceId = workflowInstanceId,
                                DiagnosticsPath = diagnosticsPath,
                                Status = WorkflowInstanceStatuses.Failed,
                                FailureReason = failure.FailureReason,
                                FailureDetails = failure.FailureDetails
                            };

                        case WorkflowOutputEvent outputEvent:
                            // Keep the latest output payload so it can be returned to callers.
                            sawWorkflowOutput = true;
                            terminalResult = outputEvent.Data as WorkflowTerminalResult;
                            finalOutput = terminalResult?.FinalOutput ?? outputEvent.Data;
                            await workflowExecutionTraceSink.RecordAsync(workflowInstanceId, request.WorkflowType, "workflow.output_emitted", new JsonObject
                            {
                                ["output"] = JsonSerializer.SerializeToNode(finalOutput),
                                ["status"] = terminalResult?.Status,
                                ["reason"] = terminalResult?.Reason
                            }, executionCancellationToken);
                            break;

                        case SuperStepCompletedEvent { CompletionInfo.Checkpoint: { } checkpoint } when store is not null:
                            // Persist checkpoints as they are produced so the workflow can resume from the latest state.
                            currentCheckpointId = checkpoint.CheckpointId;
                            checkpointRunId = checkpoint.SessionId;

                            if (!writeCheckpointsImmediately)
                            {
                                break;
                            }

                            await workflowExecutionTraceSink.RecordAsync(workflowInstanceId, request.WorkflowType, "workflow.checkpoint_created", new JsonObject
                            {
                                ["checkpointId"] = checkpoint.CheckpointId,
                                ["runId"] = checkpoint.SessionId
                            }, executionCancellationToken);
                            await workflowExecutionTraceSink.WriteCheckpointSummaryAsync(workflowInstanceId, request.WorkflowType,
                                await WorkflowCheckpointDiagnostics.BuildSummaryAsync(store, checkpoint, executionCancellationToken),
                                executionCancellationToken);

                            await workflowInstancePersistenceService.PersistWorkflowInstanceAsync(workflowInstanceId, request.WorkflowType, superStepStatus, null, null, null, currentCheckpointId,
                                checkpointRunId, workflowDefinitionService.BuildAwaitingEventDescriptors(workflowResponse.Definition, currentCheckpointId), executionCancellationToken, initiatingEvent.EventId, initiatingEvent.EventType, initiatingEvent.EventSource,
                                executionPolicyOverride: request.ExecutionPolicyOverride);
                            break;
                    }
                }

                runtimeActiveHaltingTaskNames = WorkflowExecutionTraceContext.GetActiveHaltingTaskNames();
            }
        }
        catch (OperationCanceledException) when (IsPromptShutdownInterruption(cancellationToken))
        {
            return await PersistInterruptedStartAsync(
                request,
                workflowInstanceId,
                diagnosticsPath,
                initiatingEvent,
                checkpointMode,
                failureBufferedStore,
                durableStore,
                store,
                workflowResponse.Definition,
                currentCheckpointId,
                checkpointRunId,
                runtimeActiveHaltingTaskNames);
        }

        #endregion

        #region Finalize and persist end state
        // Only treat the run as completed once the workflow emits terminal output; a checkpointed halt alone is resumable suspension.

        var observedCheckpoint = string.IsNullOrWhiteSpace(currentCheckpointId)
            ? null
            : new CheckpointInfo(checkpointRunId, currentCheckpointId);

        var finalOutcome = await WorkflowRunOutcomeResolver.ResolveAsync(
            store,
            workflowResponse.Definition,
            observedCheckpoint,
            superStepStatus,
            sawWorkflowOutput,
            terminalResult,
            cancellationToken);

        status = finalOutcome.Status;
        currentCheckpointId = finalOutcome.Checkpoint?.CheckpointId;
        checkpointRunId = finalOutcome.Checkpoint?.SessionId ?? checkpointRunId;
        var terminalFailureReason = string.Equals(status, WorkflowInstanceStatuses.Failed, StringComparison.OrdinalIgnoreCase)
            ? terminalResult?.Reason ?? "Workflow terminated with Failed status."
            : null;

        if (checkpointMode == WorkflowCheckpointingMode.Failure)
        {
            if (string.Equals(status, WorkflowInstanceStatuses.Failed, StringComparison.OrdinalIgnoreCase))
            {
                await FlushFailureBufferedCheckpointsAsync(
                    failureBufferedStore,
                    durableStore,
                    workflowInstanceId,
                    request.WorkflowType,
                    cancellationToken);
            }
            else
            {
                failureBufferedStore?.Discard();
                currentCheckpointId = null;
                checkpointRunId = workflowInstanceId;
            }
        }

        var awaitingEventsToPersist = string.Equals(status, WorkflowInstanceStatuses.Suspended, StringComparison.OrdinalIgnoreCase)
            ? finalOutcome.ActiveHaltingTaskNames.Count > 0
                ? workflowDefinitionService.BuildAwaitingEventDescriptors(workflowResponse.Definition, currentCheckpointId, finalOutcome.ActiveHaltingTaskNames)
                : runtimeActiveHaltingTaskNames.Count > 0
                    ? workflowDefinitionService.BuildAwaitingEventDescriptors(workflowResponse.Definition, currentCheckpointId, runtimeActiveHaltingTaskNames)
                    : workflowDefinitionService.BuildAwaitingEventDescriptors(workflowResponse.Definition, currentCheckpointId)
            : [];

        await workflowInstancePersistenceService.PersistWorkflowInstanceAsync(workflowInstanceId, request.WorkflowType, status, terminalFailureReason, null,
            string.Equals(status, WorkflowInstanceStatuses.Failed, StringComparison.OrdinalIgnoreCase) ? DateTimeOffset.UtcNow : null,
            currentCheckpointId, checkpointRunId,
            awaitingEventsToPersist, cancellationToken, initiatingEvent.EventId, initiatingEvent.EventType, initiatingEvent.EventSource,
            executionPolicyOverride: request.ExecutionPolicyOverride);
        await workflowExecutionTraceSink.RecordAsync(workflowInstanceId, request.WorkflowType, "workflow.completed", new JsonObject
        {
            ["status"] = status,
            ["checkpointId"] = currentCheckpointId,
            ["checkpointRunId"] = checkpointRunId,
            ["awaitingEvents"] = new JsonArray(awaitingEventsToPersist.Select(descriptor => JsonValue.Create(descriptor.TaskName)).ToArray()),
            ["reason"] = terminalResult?.Reason
        }, cancellationToken);

        return new StartWorkflowResult
        {
            Success = !string.Equals(status, WorkflowInstanceStatuses.Failed, StringComparison.OrdinalIgnoreCase),
            WorkflowInstanceId = workflowInstanceId,
            DiagnosticsPath = diagnosticsPath,
            Status = status,
            FailureReason = terminalFailureReason,
            FinalOutput = finalOutput,
        };

        #endregion
    }

    private bool IsPromptShutdownInterruption(CancellationToken callerCancellationToken)
        => orchestratorShutdownSignal.IsShutdownRequested || callerCancellationToken.IsCancellationRequested;

    private async Task<StartWorkflowResult> PersistInterruptedStartAsync(
        StartWorkflowRequest request,
        string workflowInstanceId,
        string? diagnosticsPath,
        (string? EventId, string? EventType, string? EventSource) initiatingEvent,
        WorkflowCheckpointingMode checkpointMode,
        FailureBufferedCheckpointStore? failureBufferedStore,
        JsonCheckpointStore? durableStore,
        JsonCheckpointStore? store,
        WorkflowDefinition definition,
        string? currentCheckpointId,
        string checkpointRunId,
        IReadOnlyCollection<string> runtimeActiveHaltingTaskNames)
    {
        var cleanupToken = CancellationToken.None;
        var status = WorkflowInstanceStatuses.Failed;
        var checkpointIdToPersist = currentCheckpointId;
        var checkpointRunIdToPersist = checkpointRunId;
        var awaitingEventsToPersist = new List<WorkflowAwaitingEventDescriptor>();

        if (!string.IsNullOrWhiteSpace(currentCheckpointId) && store is not null)
        {
            if (checkpointMode == WorkflowCheckpointingMode.Failure)
            {
                await FlushFailureBufferedCheckpointsAsync(
                    failureBufferedStore,
                    durableStore,
                    workflowInstanceId,
                    request.WorkflowType,
                    cleanupToken);
            }

            var observedCheckpoint = new CheckpointInfo(checkpointRunId, currentCheckpointId);
            var interruptedOutcome = await WorkflowRunOutcomeResolver.ResolveAsync(
                durableStore ?? store,
                definition,
                observedCheckpoint,
                WorkflowInstanceStatuses.Running,
                sawWorkflowOutput: false,
                terminalResult: null,
                cleanupToken);

            status = WorkflowInstanceStatuses.Suspended;
            checkpointIdToPersist = interruptedOutcome.Checkpoint?.CheckpointId ?? currentCheckpointId;
            checkpointRunIdToPersist = interruptedOutcome.Checkpoint?.SessionId ?? checkpointRunId;
            awaitingEventsToPersist = interruptedOutcome.ActiveHaltingTaskNames.Count > 0
                ? workflowDefinitionService.BuildAwaitingEventDescriptors(definition, checkpointIdToPersist, interruptedOutcome.ActiveHaltingTaskNames)
                : runtimeActiveHaltingTaskNames.Count > 0
                    ? workflowDefinitionService.BuildAwaitingEventDescriptors(definition, checkpointIdToPersist, runtimeActiveHaltingTaskNames)
                    : workflowDefinitionService.BuildAwaitingEventDescriptors(definition, checkpointIdToPersist);
        }

        await workflowInstancePersistenceService.PersistWorkflowInstanceAsync(
            workflowInstanceId,
            request.WorkflowType,
            status,
            status == WorkflowInstanceStatuses.Failed ? ShutdownInterruptedFailureReason : null,
            null,
            status == WorkflowInstanceStatuses.Failed ? DateTimeOffset.UtcNow : null,
            status == WorkflowInstanceStatuses.Suspended ? checkpointIdToPersist : null,
            checkpointRunIdToPersist,
            status == WorkflowInstanceStatuses.Suspended ? awaitingEventsToPersist : [],
            cleanupToken,
            initiatingEvent.EventId,
            initiatingEvent.EventType,
            initiatingEvent.EventSource,
            executionPolicyOverride: request.ExecutionPolicyOverride);

        await workflowExecutionTraceSink.RecordAsync(workflowInstanceId, request.WorkflowType, "workflow.shutdown_interrupted", new JsonObject
        {
            ["status"] = status,
            ["checkpointId"] = status == WorkflowInstanceStatuses.Suspended ? checkpointIdToPersist : null,
            ["checkpointRunId"] = checkpointRunIdToPersist
        }, cleanupToken);

        return new StartWorkflowResult
        {
            Success = false,
            WorkflowInstanceId = workflowInstanceId,
            DiagnosticsPath = diagnosticsPath,
            Status = status,
            FailureReason = ShutdownInterruptedFailureReason
        };
    }

    private async Task PersistReservedStartFailureAsync(
        bool isReservedStart,
        string workflowInstanceId,
        string workflowType,
        string failureReason,
        WorkflowExecutionFailureDetails? failureDetails,
        (string? EventId, string? EventType, string? EventSource) initiatingEvent,
        WorkflowExecutionPolicyOverride? executionPolicyOverride,
        CancellationToken cancellationToken)
    {
        if (!isReservedStart)
        {
            return;
        }

        await workflowInstancePersistenceService.PersistWorkflowInstanceAsync(
            workflowInstanceId,
            workflowType,
            WorkflowInstanceStatuses.Failed,
            failureReason,
            failureDetails,
            DateTimeOffset.UtcNow,
            currentCheckpointId: null,
            checkpointRunId: workflowInstanceId,
            awaitingEvents: [],
            cancellationToken: cancellationToken,
            originatingEventId: initiatingEvent.EventId,
            originatingEventType: initiatingEvent.EventType,
            originatingEventSource: initiatingEvent.EventSource,
            executionPolicyOverride: executionPolicyOverride);
    }

    private async Task FlushFailureBufferedCheckpointsAsync(
        FailureBufferedCheckpointStore? failureBufferedStore,
        JsonCheckpointStore? durableStore,
        string workflowInstanceId,
        string workflowType,
        CancellationToken cancellationToken)
    {
        if (failureBufferedStore is null || durableStore is null)
        {
            return;
        }

        var checkpoints = failureBufferedStore.BufferedCheckpoints;
        await failureBufferedStore.FlushToDurableStoreAsync(cancellationToken);

        foreach (var checkpoint in checkpoints)
        {
            await workflowExecutionTraceSink.RecordAsync(workflowInstanceId, workflowType, "workflow.checkpoint_created", new JsonObject
            {
                ["checkpointId"] = checkpoint.CheckpointId,
                ["runId"] = checkpoint.SessionId,
                ["mode"] = "Failure"
            }, cancellationToken);
            await workflowExecutionTraceSink.WriteCheckpointSummaryAsync(workflowInstanceId, workflowType,
                await WorkflowCheckpointDiagnostics.BuildSummaryAsync(durableStore, checkpoint, cancellationToken),
                cancellationToken);
        }
    }
}
