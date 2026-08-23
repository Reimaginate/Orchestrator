using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Reimaginate.Mediator;
using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.Common.Config;
using Reimaginate.Orchestrator.Common.Constants;
using Reimaginate.Orchestrator.Common.Diagnostics;
using Reimaginate.Orchestrator.Common.Requests.Internal.BuildAgentWorkflow;
using Reimaginate.Orchestrator.Common.Requests.Internal.GetWorkflowInstance;
using Reimaginate.Orchestrator.Common.Requests.Internal.ResolveAndLoadWorkflowDefinition;
using Reimaginate.Orchestrator.Common.Services.WorkflowExecution;
using Reimaginate.Orchestrator.Common.Services.WorkflowDefinitions;
using Reimaginate.Orchestrator.Common.Services.WorkflowInstancePersistence;
using Reimaginate.Orchestrator.Common.Services.WorkflowResume;
using Reimaginate.Orchestrator.Common.Services.Shutdown;
using Reimaginate.Orchestrator.Common.Models;
using Reimaginate.Orchestrator.Common.Stores.Checkpoints;
using Reimaginate.Orchestrator.DSL;
using WorkflowEventModel = Reimaginate.Orchestrator.Common.Models.WorkflowEvent;

namespace Reimaginate.Orchestrator.Common.Requests.External.ResumeWorkflow;

public class ResumeWorkflowRequestHandler(Channel<JsonObject> channel, IMediator mediator, ICheckpointStorage checkpointStorage, IWorkflowResumeRuntimeService workflowResumeRuntimeService, IWorkflowInstancePersistenceService workflowInstancePersistenceService, IWorkflowDefinitionService workflowDefinitionService, IWorkflowExecutionTraceSink workflowExecutionTraceSink, IWorkflowInstanceLockProvider workflowInstanceLockProvider, IWorkflowExecutionPolicyProvider workflowExecutionPolicyProvider, IOrchestratorShutdownSignal orchestratorShutdownSignal) : IHandler<ResumeWorkflowRequest, ResumeWorkflowResult>
{
    public const string NonResumableListenStepFailureReason = "workflow instance is not in a resumable suspended state";
    public const string InvalidInitiatingEventFailureReason = "resume request initiating event payload is malformed";
    public const string WorkflowInstanceLockedFailureReason = "workflow instance is already being executed";
    public const string WorkflowInstanceLockLostFailureReason = "workflow instance execution lock was lost";
    public const string ShutdownInterruptedFailureReason = StartWorkflow.StartWorkflowRequestHandler.ShutdownInterruptedFailureReason;

    public async Task<ResumeWorkflowResult> HandleAsync(ResumeWorkflowRequest request, CancellationToken cancellationToken)
    {
        #region Resolve workflow definition and build executable graph
        // Resolve and compile the workflow before attempting to load or replay runtime state.

        var (definitionResponse, definitionError) = await mediator.TrySend(new ResolveAndLoadWorkflowDefinitionRequest
        {
            WorkflowType = request.WorkflowType
        }, cancellationToken);

        if (definitionResponse is null || definitionError is not null || !definitionResponse.Success)
        {
            return new ResumeWorkflowResult
            {
                Success = false,
                FailureReason = definitionResponse?.FailureReason ?? definitionError?.Message ?? "Error occurred while resolving workflow definition"
            };
        }

        var (workflowResponse, ex) = await mediator.TrySend(new BuildAgentWorkflowRequest
        {
            WorkflowType = request.WorkflowType,
            Ast = definitionResponse.Ast,
            Definition = definitionResponse.Definition
        }, cancellationToken);

        if (workflowResponse is null || ex is not null || !workflowResponse.Success)
        {
            return new ResumeWorkflowResult
            {
                Success = false,
                WorkflowInstanceId = request.WorkflowInstanceId,
                FailureReason = workflowResponse?.FailureReason ?? ex?.Message ?? "Error occurred while processing request"
            };
        }

        #endregion

        #region Acquire workflow instance execution lock
        // Only one host should execute a given workflow instance at a time.

        var lockAcquireResult = await workflowInstanceLockProvider.TryAcquireAsync(request.WorkflowInstanceId, cancellationToken);
        if (!lockAcquireResult.Success)
        {
            return new ResumeWorkflowResult
            {
                Success = false,
                WorkflowInstanceId = request.WorkflowInstanceId,
                FailureReason = lockAcquireResult.FailureReason ?? WorkflowInstanceLockedFailureReason
            };
        }

        if (!lockAcquireResult.LockAcquired)
        {
            return new ResumeWorkflowResult
            {
                Success = false,
                WorkflowInstanceId = request.WorkflowInstanceId,
                FailureReason = WorkflowInstanceLockedFailureReason
            };
        }

        await using var workflowInstanceLock = lockAcquireResult.Lease
            ?? throw new InvalidOperationException("Workflow instance lock provider reported an acquired lock without returning a lease.");
        using var workflowExecutionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, workflowInstanceLock.LostToken, orchestratorShutdownSignal.ShutdownToken);
        var executionCancellationToken = workflowExecutionCancellation.Token;
        var diagnosticsPath = workflowExecutionTraceSink.GetDiagnosticsPath(request.WorkflowInstanceId);
        var checkpointMode = workflowExecutionPolicyProvider.Resolve(request.WorkflowType)
            .Apply(request.ExecutionPolicyOverride)
            .Checkpoints;
        WorkflowInstance? workflowInstanceForInterruption = null;
        JsonCheckpointStore? storeForInterruption = null;
        JsonCheckpointStore? durableStoreForInterruption = null;
        FailureBufferedCheckpointStore? failureBufferedStoreForInterruption = null;
        string? currentCheckpointIdForInterruption = null;
        string checkpointRunIdForInterruption = request.WorkflowInstanceId;
        IReadOnlyCollection<string> activeHaltingTaskNamesForInterruption = [];

        #endregion

        try
        {
            executionCancellationToken.ThrowIfCancellationRequested();

        #region Resolve checkpoint manager and validate resumable workflow instance
        // Load stored workflow state and ensure this instance is actually eligible for resuming.

        executionCancellationToken.ThrowIfCancellationRequested();

        var workflowInstance = await GetWorkflowInstanceAsync(request.WorkflowInstanceId, executionCancellationToken)
            ?? throw new FileNotFoundException($"Workflow instance '{request.WorkflowInstanceId}' could not be found.");
        workflowInstanceForInterruption = workflowInstance;

        if (!string.Equals(workflowInstance.Status, WorkflowInstanceStatuses.Suspended, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(workflowInstance.WorkflowInstanceId))
        {
            return new ResumeWorkflowResult
            {
                Success = false,
                WorkflowInstanceId = request.WorkflowInstanceId,
                FailureReason = NonResumableListenStepFailureReason
            };
        }

        var initiatingEventParsed = workflowResumeRuntimeService.TryResolveInitiatingEvent(request.Input, out var initiatingEvent, out var hasInitiatingEventInput);
        if (hasInitiatingEventInput && !initiatingEventParsed)
        {
            return new ResumeWorkflowResult
            {
                Success = false,
                WorkflowInstanceId = request.WorkflowInstanceId,
                FailureReason = InvalidInitiatingEventFailureReason
            };
        }

        if (checkpointMode == WorkflowCheckpointingMode.Disabled)
        {
            if (!CanRestartCheckpointlessSuspendedWorkflow(workflowInstance, workflowResponse.DiagnosticsGraph, allowAnyCheckpointlessAwaitingEvent: true))
            {
                return new ResumeWorkflowResult
                {
                    Success = false,
                    WorkflowInstanceId = request.WorkflowInstanceId,
                    FailureReason = NonResumableListenStepFailureReason
                };
            }

            return await RunCheckpointlessSuspendedWorkflowAsync(
                request,
                workflowResponse,
                workflowInstance,
                store: null,
                checkpointManager: null,
                checkpointMode,
                failureBufferedStore: null,
                durableStore: null,
                initiatingEventParsed ? initiatingEvent : null,
                initiatingEventParsed,
                hasInitiatingEventInput,
                diagnosticsPath,
                executionCancellationToken);
        }

        var durableStore = checkpointStorage.CreateStore();
        durableStoreForInterruption = durableStore;
        var failureBufferedStore = checkpointMode == WorkflowCheckpointingMode.Failure
            ? new FailureBufferedCheckpointStore(durableStore)
            : null;
        failureBufferedStoreForInterruption = failureBufferedStore;
        var store = failureBufferedStore ?? durableStore;
        storeForInterruption = store;
        var checkpointManager = CheckpointManager.CreateJson(store);

        var checkpointToResume = await workflowResumeRuntimeService.ResolveCheckpointToResumeAsync(durableStore, workflowInstance);
        if (checkpointToResume is null)
        {
            var allowAnyCheckpointlessAwaitingEvent = checkpointMode == WorkflowCheckpointingMode.Failure;
            if (CanRestartCheckpointlessSuspendedWorkflow(workflowInstance, workflowResponse.DiagnosticsGraph, allowAnyCheckpointlessAwaitingEvent))
            {
                return await RunCheckpointlessSuspendedWorkflowAsync(
                    request,
                    workflowResponse,
                    workflowInstance,
                    store,
                    checkpointManager,
                    checkpointMode,
                    failureBufferedStore,
                    durableStore,
                    initiatingEventParsed ? initiatingEvent : null,
                    initiatingEventParsed,
                    hasInitiatingEventInput,
                    diagnosticsPath,
                    executionCancellationToken);
            }

            return new ResumeWorkflowResult
            {
                Success = false,
                WorkflowInstanceId = request.WorkflowInstanceId,
                FailureReason = NonResumableListenStepFailureReason
            };
        }

        #endregion


        #region Resume workflow stream from checkpoint
        // Restore the checkpoint and optionally write the wake event that triggered this resume request.

        var hasQueuedRuntimeMessages = true;
        JsonElement checkpointPayload = default;
        var checkpointPayloadLoaded = false;
        try
        {
            checkpointPayload = await store.RetrieveCheckpointAsync(checkpointToResume.SessionId, checkpointToResume);
            checkpointPayloadLoaded = true;
            hasQueuedRuntimeMessages = workflowResumeRuntimeService.HasQueuedRuntimeMessages(checkpointPayload);
        }
        catch
        {
            // Fail safe when payload cannot be read/parsing assumptions no longer hold.
            hasQueuedRuntimeMessages = true;
        }

        if (initiatingEventParsed
            && initiatingEvent is not null
            && workflowResumeRuntimeService.TryBuildResumeWakeMessage(initiatingEvent, out var message))
        {
            await channel.Writer.WriteAsync(message, executionCancellationToken);
        }

        var currentCheckpointId = checkpointToResume.CheckpointId;
        var checkpointRunId = checkpointToResume.SessionId;
        currentCheckpointIdForInterruption = currentCheckpointId;
        checkpointRunIdForInterruption = checkpointRunId;
        var status = WorkflowInstanceStatuses.Running;
        workflowResponse.DiagnosticsGraph["workflowType"] = request.WorkflowType;

        await workflowExecutionTraceSink.WriteGraphAsync(request.WorkflowType, workflowResponse.DiagnosticsGraph, request.WorkflowInstanceId, executionCancellationToken);
        await workflowExecutionTraceSink.RecordAsync(request.WorkflowInstanceId, request.WorkflowType, "workflow.resume_requested", new JsonObject
        {
            ["checkpointId"] = checkpointToResume.CheckpointId,
            ["checkpointRunId"] = checkpointToResume.SessionId,
            ["hasQueuedRuntimeMessages"] = hasQueuedRuntimeMessages,
            ["hasInitiatingEventInput"] = hasInitiatingEventInput,
            ["initiatingEvent"] = initiatingEventParsed && initiatingEvent is not null
                ? JsonSerializer.SerializeToNode(initiatingEvent)
                : null
        }, executionCancellationToken);
        await workflowExecutionTraceSink.WriteCheckpointSummaryAsync(request.WorkflowInstanceId, request.WorkflowType,
            await WorkflowCheckpointDiagnostics.BuildSummaryAsync(store, checkpointToResume, executionCancellationToken),
            executionCancellationToken);

        #endregion


        #region Observe run stream and persist intermediate checkpoints
        // Track stream outcomes and persist checkpoints as each super-step completes.

        var superStepStatus = WorkflowInstanceStatuses.Running;
        var sawWorkflowOutput = false;
        IReadOnlyCollection<string> runtimeActiveHaltingTaskNames = [];
        object? finalOutput = null;
        WorkflowExecutionFailureInfo? failure = null;
        WorkflowTerminalResult? terminalResult = null;

        using (WorkflowExecutionTraceContext.BeginScope(workflowExecutionTraceSink, request.WorkflowType, request.WorkflowInstanceId))
        {
            await using var run = await InProcessExecution.ResumeStreamingAsync(
                workflowResponse.Workflow,
                fromCheckpoint: checkpointToResume,
                checkpointManager: checkpointManager,
                cancellationToken: executionCancellationToken);

            if (!hasQueuedRuntimeMessages)
            {
                var bootstrapPayload = checkpointPayloadLoaded
                    && workflowResumeRuntimeService.TryBuildResumeBootstrapMessage(checkpointPayload, out var checkpointBootstrapMessage)
                        ? checkpointBootstrapMessage
                        : new JsonObject();

                await workflowExecutionTraceSink.RecordAsync(request.WorkflowInstanceId, request.WorkflowType, "workflow.resume_bootstrap_sent", new JsonObject
                {
                    ["payload"] = bootstrapPayload.DeepClone()
                }, executionCancellationToken);
                await run.TrySendMessageAsync(bootstrapPayload);
            }

            await foreach (var evt in run.WatchStreamAsync(executionCancellationToken))
            {
                switch (evt)
                {
                    case SuperStepStartedEvent:
                        superStepStatus = WorkflowInstanceStatuses.Running;
                        await workflowExecutionTraceSink.RecordAsync(request.WorkflowInstanceId, request.WorkflowType, "workflow.super_step_started", new JsonObject(), executionCancellationToken);
                        break;

                    case WorkflowErrorEvent workflowErrorEvent:
                        superStepStatus = WorkflowInstanceStatuses.Failed;
                        failure = WorkflowExecutionFailureInfoBuilder.Build("Workflow failed while processing resume request.", workflowErrorEvent.Exception);
                        await workflowExecutionTraceSink.RecordAsync(request.WorkflowInstanceId, request.WorkflowType, "workflow.error", failure.ToTracePayload(), executionCancellationToken);
                        break;

                    case WorkflowOutputEvent outputEvent:
                        sawWorkflowOutput = true;
                        terminalResult = outputEvent.Data as WorkflowTerminalResult;
                        finalOutput = terminalResult?.FinalOutput ?? outputEvent.Data;
                        await workflowExecutionTraceSink.RecordAsync(request.WorkflowInstanceId, request.WorkflowType, "workflow.output_emitted", new JsonObject
                        {
                            ["output"] = JsonNode.Parse(JsonSerializer.Serialize(finalOutput)),
                            ["status"] = terminalResult?.Status,
                            ["reason"] = terminalResult?.Reason
                        }, executionCancellationToken);
                        break;

                    case SuperStepCompletedEvent superStepCompletedEvent when superStepCompletedEvent.CompletionInfo?.Checkpoint is { } checkpoint:
                        currentCheckpointId = checkpoint.CheckpointId;
                        checkpointRunId = checkpoint.SessionId;
                        currentCheckpointIdForInterruption = currentCheckpointId;
                        checkpointRunIdForInterruption = checkpointRunId;

                        if (checkpointMode != WorkflowCheckpointingMode.Enabled)
                        {
                            break;
                        }

                        await workflowExecutionTraceSink.RecordAsync(request.WorkflowInstanceId, request.WorkflowType, "workflow.checkpoint_created", new JsonObject
                        {
                            ["checkpointId"] = checkpoint.CheckpointId,
                            ["runId"] = checkpoint.SessionId
                        }, executionCancellationToken);
                        await workflowExecutionTraceSink.WriteCheckpointSummaryAsync(request.WorkflowInstanceId, request.WorkflowType,
                            await WorkflowCheckpointDiagnostics.BuildSummaryAsync(store, checkpoint, executionCancellationToken),
                            executionCancellationToken);

                        await workflowInstancePersistenceService.PersistWorkflowInstanceAsync(request.WorkflowInstanceId, request.WorkflowType, WorkflowInstanceStatuses.Running,
                            null, null, null, currentCheckpointId, checkpointRunId, [], executionCancellationToken, metadata: workflowInstance.Metadata,
                            executionPolicyOverride: request.ExecutionPolicyOverride);
                        break;
                }
            }

            runtimeActiveHaltingTaskNames = WorkflowExecutionTraceContext.GetActiveHaltingTaskNames();
            activeHaltingTaskNamesForInterruption = runtimeActiveHaltingTaskNames;
        }

        #endregion
        executionCancellationToken.ThrowIfCancellationRequested();


        #region Finalize workflow status and return response
        // A resumed run is only complete once it emits terminal output; checkpointed halts remain suspended.

        if (string.Equals(superStepStatus, WorkflowInstanceStatuses.Failed, StringComparison.OrdinalIgnoreCase))
        {
            status = WorkflowInstanceStatuses.Failed;
        }

        var observedCheckpoint = string.IsNullOrWhiteSpace(currentCheckpointId)
            ? checkpointToResume
            : new CheckpointInfo(checkpointRunId, currentCheckpointId);

        var finalOutcome = await WorkflowRunOutcomeResolver.ResolveAsync(
            store,
            workflowResponse.Definition,
            observedCheckpoint,
            superStepStatus,
            sawWorkflowOutput,
            terminalResult,
            executionCancellationToken);

        status = finalOutcome.Status;
        currentCheckpointId = finalOutcome.Checkpoint?.CheckpointId;
        checkpointRunId = finalOutcome.Checkpoint?.SessionId ?? checkpointRunId;
        executionCancellationToken.ThrowIfCancellationRequested();
        var terminalFailureReason = string.Equals(status, WorkflowInstanceStatuses.Failed, StringComparison.OrdinalIgnoreCase)
            ? terminalResult?.Reason ?? failure?.FailureReason ?? "Workflow failed while processing resume request."
            : null;

        if (checkpointMode == WorkflowCheckpointingMode.Failure)
        {
            if (string.Equals(status, WorkflowInstanceStatuses.Failed, StringComparison.OrdinalIgnoreCase))
            {
                await FlushFailureBufferedCheckpointsAsync(
                    failureBufferedStore,
                    durableStore,
                    request.WorkflowInstanceId,
                    request.WorkflowType,
                    executionCancellationToken);
            }
            else
            {
                failureBufferedStore?.Discard();
                currentCheckpointId = null;
                checkpointRunId = request.WorkflowInstanceId;
            }
        }

        var awaitingEventsToPersist = string.Equals(status, WorkflowInstanceStatuses.Suspended, StringComparison.OrdinalIgnoreCase)
            ? finalOutcome.ActiveHaltingTaskNames.Count > 0
                ? workflowDefinitionService.BuildAwaitingEventDescriptors(workflowResponse.Definition, currentCheckpointId, finalOutcome.ActiveHaltingTaskNames)
                : runtimeActiveHaltingTaskNames.Count > 0
                    ? workflowDefinitionService.BuildAwaitingEventDescriptors(workflowResponse.Definition, currentCheckpointId, runtimeActiveHaltingTaskNames)
                    : workflowDefinitionService.BuildAwaitingEventDescriptors(workflowResponse.Definition, currentCheckpointId)
            : [];

        await workflowInstancePersistenceService.PersistWorkflowInstanceAsync(request.WorkflowInstanceId, request.WorkflowType, status,
            terminalFailureReason,
            string.Equals(status, WorkflowInstanceStatuses.Failed, StringComparison.OrdinalIgnoreCase) ? failure?.FailureDetails : null,
            string.Equals(status, WorkflowInstanceStatuses.Failed, StringComparison.OrdinalIgnoreCase) ? DateTimeOffset.UtcNow : null,
            currentCheckpointId, checkpointRunId, awaitingEventsToPersist, executionCancellationToken, metadata: workflowInstance.Metadata,
            executionPolicyOverride: request.ExecutionPolicyOverride);
        await workflowExecutionTraceSink.RecordAsync(request.WorkflowInstanceId, request.WorkflowType, "workflow.completed", new JsonObject
        {
            ["status"] = status,
            ["checkpointId"] = currentCheckpointId,
            ["checkpointRunId"] = checkpointRunId,
            ["awaitingEvents"] = new JsonArray(awaitingEventsToPersist.Select(descriptor => JsonValue.Create(descriptor.TaskName)).ToArray()),
            ["reason"] = terminalResult?.Reason
        }, executionCancellationToken);

        return new ResumeWorkflowResult
        {
            WorkflowInstanceId = request.WorkflowInstanceId,
            DiagnosticsPath = diagnosticsPath,
            Status = status,
            Success = !string.Equals(status, WorkflowInstanceStatuses.Failed, StringComparison.OrdinalIgnoreCase),
            FailureDetails = string.Equals(status, WorkflowInstanceStatuses.Failed, StringComparison.OrdinalIgnoreCase)
                ? failure?.FailureDetails
                : null,
            FailureReason = terminalFailureReason,
            FinalOutput = finalOutput
        };

        #endregion
        }
        catch (Exception) when (workflowInstanceLock.LostToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                await workflowExecutionTraceSink.RecordAsync(request.WorkflowInstanceId, request.WorkflowType, "workflow.lock_lost", new JsonObject(), CancellationToken.None);
            }
            catch
            {
            }

            return new ResumeWorkflowResult
            {
                Success = false,
                WorkflowInstanceId = request.WorkflowInstanceId,
                DiagnosticsPath = diagnosticsPath,
                FailureReason = WorkflowInstanceLockLostFailureReason
            };
        }
        catch (OperationCanceledException) when (IsPromptShutdownInterruption(cancellationToken))
        {
            return await PersistInterruptedResumeAsync(
                request,
                diagnosticsPath,
                workflowResponse.Definition,
                workflowInstanceForInterruption,
                checkpointMode,
                failureBufferedStoreForInterruption,
                durableStoreForInterruption,
                storeForInterruption,
                currentCheckpointIdForInterruption,
                checkpointRunIdForInterruption,
                activeHaltingTaskNamesForInterruption);
        }
    }

    private async Task<ResumeWorkflowResult> RunCheckpointlessSuspendedWorkflowAsync(
        ResumeWorkflowRequest request,
        BuildAgentWorkflowResponse workflowResponse,
        WorkflowInstance workflowInstance,
        JsonCheckpointStore? store,
        CheckpointManager? checkpointManager,
        WorkflowCheckpointingMode checkpointMode,
        FailureBufferedCheckpointStore? failureBufferedStore,
        JsonCheckpointStore? durableStore,
        WorkflowEventModel? initiatingEvent,
        bool initiatingEventParsed,
        bool hasInitiatingEventInput,
        string? diagnosticsPath,
        CancellationToken cancellationToken)
    {
        var workflowInput = new JsonObject();
        if (initiatingEventParsed && initiatingEvent is not null)
        {
            if (!workflowResumeRuntimeService.TryBuildResumeWakeMessage(initiatingEvent, out workflowInput))
            {
                return new ResumeWorkflowResult
                {
                    Success = false,
                    WorkflowInstanceId = request.WorkflowInstanceId,
                    FailureReason = InvalidInitiatingEventFailureReason
                };
            }
        }

        workflowInput["__trigger"] = "resume";

        string? currentCheckpointId = null;
        var checkpointRunId = request.WorkflowInstanceId;
        var status = WorkflowInstanceStatuses.Running;
        var superStepStatus = WorkflowInstanceStatuses.Running;
        var sawWorkflowOutput = false;
        IReadOnlyCollection<string> runtimeActiveHaltingTaskNames = [];
        object? finalOutput = null;
        WorkflowExecutionFailureInfo? failure = null;
        WorkflowTerminalResult? terminalResult = null;

        workflowResponse.DiagnosticsGraph["workflowType"] = request.WorkflowType;
        await workflowExecutionTraceSink.WriteGraphAsync(request.WorkflowType, workflowResponse.DiagnosticsGraph, request.WorkflowInstanceId, cancellationToken);
        await workflowExecutionTraceSink.RecordAsync(request.WorkflowInstanceId, request.WorkflowType, "workflow.resume_requested", new JsonObject
        {
            ["checkpointId"] = null,
            ["checkpointRunId"] = checkpointRunId,
            ["hasQueuedRuntimeMessages"] = false,
            ["hasInitiatingEventInput"] = hasInitiatingEventInput,
            ["initiatingEvent"] = initiatingEventParsed && initiatingEvent is not null
                ? JsonSerializer.SerializeToNode(initiatingEvent)
                : null,
            ["checkpointlessRestart"] = true
        }, cancellationToken);

        await workflowInstancePersistenceService.PersistWorkflowInstanceAsync(
            request.WorkflowInstanceId,
            request.WorkflowType,
            WorkflowInstanceStatuses.Running,
            null,
            null,
            null,
            currentCheckpointId,
            checkpointRunId,
            [],
            cancellationToken,
            metadata: workflowInstance.Metadata,
            executionPolicyOverride: request.ExecutionPolicyOverride);

        using (WorkflowExecutionTraceContext.BeginScope(workflowExecutionTraceSink, request.WorkflowType, request.WorkflowInstanceId))
        {
            await using var run = checkpointManager is null
                ? await InProcessExecution.RunStreamingAsync(
                    workflowResponse.Workflow,
                    input: workflowInput,
                    cancellationToken: cancellationToken)
                : await InProcessExecution.RunStreamingAsync(
                    workflowResponse.Workflow,
                    workflowInput,
                    checkpointManager,
                    request.WorkflowInstanceId,
                    cancellationToken);

            await foreach (var evt in run.WatchStreamAsync(cancellationToken))
            {
                switch (evt)
                {
                    case SuperStepStartedEvent:
                        superStepStatus = WorkflowInstanceStatuses.Running;
                        await workflowExecutionTraceSink.RecordAsync(request.WorkflowInstanceId, request.WorkflowType, "workflow.super_step_started", new JsonObject(), cancellationToken);
                        break;

                    case WorkflowErrorEvent workflowErrorEvent:
                        superStepStatus = WorkflowInstanceStatuses.Failed;
                        failure = WorkflowExecutionFailureInfoBuilder.Build("Workflow failed while processing resume request.", workflowErrorEvent.Exception);
                        await workflowExecutionTraceSink.RecordAsync(request.WorkflowInstanceId, request.WorkflowType, "workflow.error", failure.ToTracePayload(), cancellationToken);
                        break;

                    case WorkflowOutputEvent outputEvent:
                        sawWorkflowOutput = true;
                        terminalResult = outputEvent.Data as WorkflowTerminalResult;
                        finalOutput = terminalResult?.FinalOutput ?? outputEvent.Data;
                        await workflowExecutionTraceSink.RecordAsync(request.WorkflowInstanceId, request.WorkflowType, "workflow.output_emitted", new JsonObject
                        {
                            ["output"] = JsonSerializer.SerializeToNode(finalOutput),
                            ["status"] = terminalResult?.Status,
                            ["reason"] = terminalResult?.Reason
                        }, cancellationToken);
                        break;

                    case SuperStepCompletedEvent superStepCompletedEvent when superStepCompletedEvent.CompletionInfo?.Checkpoint is { } checkpoint && store is not null:
                        currentCheckpointId = checkpoint.CheckpointId;
                        checkpointRunId = checkpoint.SessionId;

                        if (checkpointMode != WorkflowCheckpointingMode.Enabled)
                        {
                            break;
                        }

                        await workflowExecutionTraceSink.RecordAsync(request.WorkflowInstanceId, request.WorkflowType, "workflow.checkpoint_created", new JsonObject
                        {
                            ["checkpointId"] = checkpoint.CheckpointId,
                            ["runId"] = checkpoint.SessionId
                        }, cancellationToken);
                        await workflowExecutionTraceSink.WriteCheckpointSummaryAsync(request.WorkflowInstanceId, request.WorkflowType,
                            await WorkflowCheckpointDiagnostics.BuildSummaryAsync(store, checkpoint, cancellationToken),
                            cancellationToken);

                        await workflowInstancePersistenceService.PersistWorkflowInstanceAsync(
                            request.WorkflowInstanceId,
                            request.WorkflowType,
                            WorkflowInstanceStatuses.Running,
                            null,
                            null,
                            null,
                            currentCheckpointId,
                            checkpointRunId,
                            [],
                            cancellationToken,
                            metadata: workflowInstance.Metadata,
                            executionPolicyOverride: request.ExecutionPolicyOverride);
                        break;
                }
            }

            runtimeActiveHaltingTaskNames = WorkflowExecutionTraceContext.GetActiveHaltingTaskNames();
        }

        if (string.Equals(superStepStatus, WorkflowInstanceStatuses.Failed, StringComparison.OrdinalIgnoreCase))
        {
            status = WorkflowInstanceStatuses.Failed;
        }

        var observedCheckpoint = string.IsNullOrWhiteSpace(currentCheckpointId)
            ? null
            : new CheckpointInfo(checkpointRunId, currentCheckpointId);

        var finalOutcome = await WorkflowRunOutcomeResolver.ResolveAsync(
            store,
            workflowResponse.Definition,
            observedCheckpoint,
            status,
            sawWorkflowOutput,
            terminalResult,
            cancellationToken);

        status = finalOutcome.Status;
        currentCheckpointId = finalOutcome.Checkpoint?.CheckpointId;
        checkpointRunId = finalOutcome.Checkpoint?.SessionId ?? checkpointRunId;
        var terminalFailureReason = string.Equals(status, WorkflowInstanceStatuses.Failed, StringComparison.OrdinalIgnoreCase)
            ? terminalResult?.Reason ?? failure?.FailureReason ?? "Workflow failed while processing resume request."
            : null;

        if (checkpointMode == WorkflowCheckpointingMode.Failure)
        {
            if (string.Equals(status, WorkflowInstanceStatuses.Failed, StringComparison.OrdinalIgnoreCase))
            {
                await FlushFailureBufferedCheckpointsAsync(
                    failureBufferedStore,
                    durableStore,
                    request.WorkflowInstanceId,
                    request.WorkflowType,
                    cancellationToken);
            }
            else
            {
                failureBufferedStore?.Discard();
                currentCheckpointId = null;
                checkpointRunId = request.WorkflowInstanceId;
            }
        }

        var awaitingEventsToPersist = string.Equals(status, WorkflowInstanceStatuses.Suspended, StringComparison.OrdinalIgnoreCase)
            ? finalOutcome.ActiveHaltingTaskNames.Count > 0
                ? workflowDefinitionService.BuildAwaitingEventDescriptors(workflowResponse.Definition, currentCheckpointId, finalOutcome.ActiveHaltingTaskNames)
                : runtimeActiveHaltingTaskNames.Count > 0
                    ? workflowDefinitionService.BuildAwaitingEventDescriptors(workflowResponse.Definition, currentCheckpointId, runtimeActiveHaltingTaskNames)
                    : workflowDefinitionService.BuildAwaitingEventDescriptors(workflowResponse.Definition, currentCheckpointId)
            : [];

        await workflowInstancePersistenceService.PersistWorkflowInstanceAsync(
            request.WorkflowInstanceId,
            request.WorkflowType,
            status,
            terminalFailureReason,
            string.Equals(status, WorkflowInstanceStatuses.Failed, StringComparison.OrdinalIgnoreCase) ? failure?.FailureDetails : null,
            string.Equals(status, WorkflowInstanceStatuses.Failed, StringComparison.OrdinalIgnoreCase) ? DateTimeOffset.UtcNow : null,
            currentCheckpointId,
            checkpointRunId,
            awaitingEventsToPersist,
            cancellationToken,
            metadata: workflowInstance.Metadata,
            executionPolicyOverride: request.ExecutionPolicyOverride);
        await workflowExecutionTraceSink.RecordAsync(request.WorkflowInstanceId, request.WorkflowType, "workflow.completed", new JsonObject
        {
            ["status"] = status,
            ["checkpointId"] = currentCheckpointId,
            ["checkpointRunId"] = checkpointRunId,
            ["awaitingEvents"] = new JsonArray(awaitingEventsToPersist.Select(descriptor => JsonValue.Create(descriptor.TaskName)).ToArray()),
            ["reason"] = terminalResult?.Reason
        }, cancellationToken);

        return new ResumeWorkflowResult
        {
            WorkflowInstanceId = request.WorkflowInstanceId,
            DiagnosticsPath = diagnosticsPath,
            Status = status,
            Success = !string.Equals(status, WorkflowInstanceStatuses.Failed, StringComparison.OrdinalIgnoreCase),
            FailureDetails = string.Equals(status, WorkflowInstanceStatuses.Failed, StringComparison.OrdinalIgnoreCase)
                ? failure?.FailureDetails
                : null,
            FailureReason = terminalFailureReason,
            FinalOutput = finalOutput
        };
    }

    private static bool CanRestartCheckpointlessSuspendedWorkflow(WorkflowInstance workflowInstance, JsonObject diagnosticsGraph, bool allowAnyCheckpointlessAwaitingEvent)
    {
        if (workflowInstance.AwaitingEvents.Count == 0
            || workflowInstance.AwaitingEvents.Any(descriptor => !string.IsNullOrWhiteSpace(descriptor.CheckpointId)))
        {
            return false;
        }

        if (allowAnyCheckpointlessAwaitingEvent)
        {
            return true;
        }

        var startTaskName = diagnosticsGraph["startTaskName"]?.GetValue<string>();
        return !string.IsNullOrWhiteSpace(startTaskName)
            && workflowInstance.AwaitingEvents.All(descriptor => string.Equals(descriptor.TaskName, startTaskName, StringComparison.Ordinal));
    }

    private bool IsPromptShutdownInterruption(CancellationToken callerCancellationToken)
        => orchestratorShutdownSignal.IsShutdownRequested || callerCancellationToken.IsCancellationRequested;

    private async Task<ResumeWorkflowResult> PersistInterruptedResumeAsync(
        ResumeWorkflowRequest request,
        string? diagnosticsPath,
        WorkflowDefinition definition,
        WorkflowInstance? workflowInstance,
        WorkflowCheckpointingMode checkpointMode,
        FailureBufferedCheckpointStore? failureBufferedStore,
        JsonCheckpointStore? durableStore,
        JsonCheckpointStore? store,
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
                    request.WorkflowInstanceId,
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
            request.WorkflowInstanceId,
            request.WorkflowType,
            status,
            status == WorkflowInstanceStatuses.Failed ? ShutdownInterruptedFailureReason : null,
            null,
            status == WorkflowInstanceStatuses.Failed ? DateTimeOffset.UtcNow : null,
            status == WorkflowInstanceStatuses.Suspended ? checkpointIdToPersist : null,
            checkpointRunIdToPersist,
            status == WorkflowInstanceStatuses.Suspended ? awaitingEventsToPersist : [],
            cleanupToken,
            metadata: workflowInstance?.Metadata,
            executionPolicyOverride: request.ExecutionPolicyOverride);

        await workflowExecutionTraceSink.RecordAsync(request.WorkflowInstanceId, request.WorkflowType, "workflow.shutdown_interrupted", new JsonObject
        {
            ["status"] = status,
            ["checkpointId"] = status == WorkflowInstanceStatuses.Suspended ? checkpointIdToPersist : null,
            ["checkpointRunId"] = checkpointRunIdToPersist
        }, cleanupToken);

        return new ResumeWorkflowResult
        {
            Success = false,
            WorkflowInstanceId = request.WorkflowInstanceId,
            DiagnosticsPath = diagnosticsPath,
            Status = status,
            FailureReason = ShutdownInterruptedFailureReason
        };
    }

    private async Task<WorkflowInstance?> GetWorkflowInstanceAsync(string workflowInstanceId, CancellationToken cancellationToken)
    {
        #region Load workflow instance from persistence
        // Resolve the stored workflow instance used by resume precondition checks.

        var (response, error) = await mediator.TrySend(new GetWorkflowInstanceRequest
        {
            WorkflowInstanceId = workflowInstanceId
        }, cancellationToken);

        if (response is null || error is not null || !response.Success)
        {
            return null;
        }

        return response.WorkflowInstance;

        #endregion
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
