using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Reimaginate.Mediator;
using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.Common.Config;
using Reimaginate.Orchestrator.Common.Constants;
using Reimaginate.Orchestrator.Common.Diagnostics;
using Reimaginate.Orchestrator.Common.Helpers;
using Reimaginate.Orchestrator.Common.Models;
using Reimaginate.Orchestrator.Common.Requests.External.ResumeWorkflow;
using Reimaginate.Orchestrator.Common.Requests.External.StartWorkflow;
using Reimaginate.Orchestrator.Common.Services.WorkflowCorrelation;
using Reimaginate.Orchestrator.Common.Services.WorkflowEventDispatch;
using Reimaginate.Orchestrator.Common.Services.WorkflowEventProcessing;
using Reimaginate.Orchestrator.Common.Services.WorkflowEventReceivers;
using Reimaginate.Orchestrator.Common.Services.WorkflowExecution;
using Reimaginate.Orchestrator.Common.Services.WorkflowInstanceMutation;
using Reimaginate.Orchestrator.Common.Services.Shutdown;

namespace Reimaginate.Orchestrator.Common.Requests.External.ProcessEvents;

public class ProcessEventsRequestHandler(
    IConfiguration config,
    IWorkflowEnvironmentProvider workflowEnvironmentProvider,
    IWorkflowEventReceiverService workflowEventReceiverService,
    IWorkflowEventDispatchService workflowEventDispatchService,
    IWorkflowCorrelationService workflowCorrelationService,
    IWorkflowEventProcessingService workflowEventProcessingService,
    IWorkflowInstanceStore workflowInstanceStore,
    IWorkflowInstanceMutationService workflowInstanceMutationService,
    IWorkflowExecutionTraceSink workflowExecutionTraceSink,
    IWorkflowExecutionPolicyProvider workflowExecutionPolicyProvider,
    IOrchestratorShutdownSignal orchestratorShutdownSignal,
    IMediator mediator) : IHandler<ProcessEventsRequest, ProcessEventsResponse>
{
    private static readonly Counter<long> TerminalWorkflowObservationCounter = new Meter("Reimaginate.Orchestrator.Common")
        .CreateCounter<long>("workflow.resume.guard.terminal_observation");
    private static readonly TimeSpan HeldMessageRenewalInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReceiveWaitTime = TimeSpan.FromSeconds(5);
    private const int ReceiveBatchSize = 1000;
    private const int DefaultMaxConcurrency = 1;
    private const string EventProcessingConfigSection = "Orchestrator:EventProcessing";
    private const string EventProcessingMaxConcurrencyEnvironmentKey = "Orchestrator:EventProcessing:MaxConcurrency";
    private const string NoListeningWorkflowDeadLetterReason = "NoListeningWorkflow";
    private const string NoAwaitingWorkflowInstanceDeadLetterReason = "NoAwaitingWorkflowInstance";

    public async Task<ProcessEventsResponse> HandleAsync(ProcessEventsRequest request, CancellationToken cancellationToken)
    {
        var heldMessages = new List<WorkflowReceivedMessage>();
        var heldMessagesGate = new object();
        var summary = new ProcessEventsLogSummary();
        using var summaryScope = summary.BeginScope();
        string? queue = null;

        try
        {
            var settings = ResolveProcessingSettings(request);
            if (!settings.Success)
            {
                return new ProcessEventsResponse
                {
                    Success = false,
                    FailureReason = settings.FailureReason
                };
            }

            queue = settings.QueueName!;
            var maxConcurrency = settings.MaxConcurrency;
            var diagnosticsEnabled = IsDiagnosticsEnabled(request);
            var lastHeldMessageRenewal = DateTimeOffset.UtcNow;
            var failures = new List<Exception>();

            WriteLog(
                ("stage", "start"),
                ("queue", queue),
                ("batchSize", ReceiveBatchSize),
                ("maxWaitSeconds", ReceiveWaitTime.TotalSeconds),
                ("maxConcurrency", maxConcurrency),
                ("dryRun", request.DryRun ? true : null));

            try
            {
                while (true)
                {
                    if (orchestratorShutdownSignal.IsShutdownRequested)
                    {
                        WriteLog(
                            ("stage", "receive"),
                            ("queue", queue),
                            ("outcome", "shutdown_requested"));
                        break;
                    }

                    lastHeldMessageRenewal = await RenewHeldMessagesIfDueAsync(
                        heldMessages,
                        heldMessagesGate,
                        lastHeldMessageRenewal,
                        force: false,
                        cancellationToken);

                    var messages = await workflowEventReceiverService.ReceiveMessagesAsync(queue, ReceiveBatchSize, maxWaitTime: ReceiveWaitTime, cancellationToken: cancellationToken);
                    summary.AddReceived(messages.Count);

                    WriteLog(
                        ("stage", "receive"),
                        ("queue", queue),
                        ("received", messages.Count),
                        ("outcome", messages.Count == 0 ? "empty" : null));

                    if (messages.Count == 0)
                    {
                        break;
                    }

                    var batchFailures = await ProcessBatchAsync(
                        queue,
                        messages,
                        heldMessages,
                        heldMessagesGate,
                        summary,
                        maxConcurrency,
                        request.DryRun,
                        diagnosticsEnabled,
                        request.ExecutionPolicyOverride,
                        cancellationToken);

                    failures.AddRange(batchFailures);
                    if (batchFailures.Count > 0)
                    {
                        break;
                    }

                    if (request.DryRun)
                    {
                        break;
                    }
                }
            }
            finally
            {
                await ReleaseHeldMessagesAsync(
                    workflowEventReceiverService,
                    heldMessages,
                    heldMessagesGate,
                    CancellationToken.None);
            }

            if (failures.Count > 0)
            {
                return new ProcessEventsResponse
                {
                    Success = false,
                    FailureReason = BuildFailureReason(failures)
                };
            }

            return new ProcessEventsResponse
            {
                Success = true
            };
        }
        catch (Exception ex)
        {
            return new ProcessEventsResponse
            {
                Success = false,
                FailureReason = ex.Message
            };
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(queue))
            {
                WriteLog(
                    ("stage", "summary"),
                    ("queue", queue),
                    ("received", summary.Received),
                    ("completed", summary.Completed),
                    ("held", summary.Held),
                    ("deadLettered", summary.DeadLettered),
                    ("failed", summary.Failed),
                    ("started", summary.Started),
                    ("resumed", summary.Resumed),
                    ("observed", summary.Observed),
                    ("invalid", summary.Invalid),
                    ("deduplicated", summary.Deduplicated),
                    ("stale", summary.Stale),
                    ("terminalDuplicate", summary.TerminalDuplicate));
            }
        }
    }

    private ProcessEventsSettings ResolveProcessingSettings(ProcessEventsRequest request)
    {
        var eventProcessingSection = config.GetSection(EventProcessingConfigSection);
        var queueName = request.QueueName ?? eventProcessingSection["QueueName"];
        if (string.IsNullOrWhiteSpace(queueName))
        {
            return ProcessEventsSettings.Fail("Event processing queue name is required.");
        }

        var maxConcurrency = request.MaxConcurrency;
        if (!maxConcurrency.HasValue)
        {
            var environment = workflowEnvironmentProvider.BuildEnvironment();
            var environmentMaxConcurrency = ReadEnvironmentValue(environment, EventProcessingMaxConcurrencyEnvironmentKey);
            if (!string.IsNullOrWhiteSpace(environmentMaxConcurrency))
            {
                if (!TryParsePositiveMaxConcurrency(
                        environmentMaxConcurrency,
                        "Workflow environment Orchestrator:EventProcessing:MaxConcurrency",
                        out var parsedMaxConcurrency,
                        out var failureReason))
                {
                    return ProcessEventsSettings.Fail(failureReason);
                }

                maxConcurrency = parsedMaxConcurrency;
            }
            else
            {
                var configuredMaxConcurrency = eventProcessingSection["MaxConcurrency"];
                if (!string.IsNullOrWhiteSpace(configuredMaxConcurrency))
                {
                    if (!TryParsePositiveMaxConcurrency(
                            configuredMaxConcurrency,
                            "Orchestrator:EventProcessing:MaxConcurrency",
                            out var parsedMaxConcurrency,
                            out var failureReason))
                    {
                        return ProcessEventsSettings.Fail(failureReason);
                    }

                    maxConcurrency = parsedMaxConcurrency;
                }
            }
        }

        maxConcurrency ??= DefaultMaxConcurrency;
        if (maxConcurrency.Value < 1)
        {
            return ProcessEventsSettings.Fail("ProcessEvents MaxConcurrency must be greater than or equal to 1.");
        }

        return ProcessEventsSettings.Ok(queueName, maxConcurrency.Value);
    }

    private static bool IsDiagnosticsEnabled(ProcessEventsRequest request)
        => request.DryRun || request.DiagnosticsMode == ProcessEventsDiagnosticsMode.Summary;

    private static string? ReadEnvironmentValue(JsonObject environment, string key)
    {
        if (!environment.TryGetPropertyValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            JsonValue jsonValue when jsonValue.TryGetValue<string>(out var stringValue) => stringValue,
            JsonValue jsonValue when jsonValue.TryGetValue<int>(out var intValue) => intValue.ToString(CultureInfo.InvariantCulture),
            _ => value.ToJsonString()
        };
    }

    private static bool TryParsePositiveMaxConcurrency(string value, string sourceName, out int maxConcurrency, out string failureReason)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out maxConcurrency) || maxConcurrency < 1)
        {
            failureReason = $"{sourceName} must be a positive integer.";
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private async Task<IReadOnlyList<Exception>> ProcessBatchAsync(
        string queue,
        IReadOnlyList<WorkflowReceivedMessage> messages,
        List<WorkflowReceivedMessage> heldMessages,
        object heldMessagesGate,
        ProcessEventsLogSummary summary,
        int maxConcurrency,
        bool dryRun,
        bool diagnosticsEnabled,
        WorkflowExecutionPolicyOverride? executionPolicyOverride,
        CancellationToken cancellationToken)
    {
        var messageStates = new List<ProcessEventsMessageState>(messages.Count);
        var workItems = new List<ProcessEventsWorkflowWorkItem>();

        foreach (var message in messages)
        {
            var state = await BuildMessageStateAsync(queue, message, summary, dryRun, diagnosticsEnabled, executionPolicyOverride, cancellationToken);
            if (state is null)
            {
                continue;
            }

            messageStates.Add(state);
            workItems.AddRange(state.WorkItems);
        }

        var failures = new List<Exception>();
        if (workItems.Count > 0 && !dryRun)
        {
            using var concurrencyGate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
            var processingTasks = workItems.Select(workItem => ProcessWorkItemWithConcurrencyAsync(
                queue,
                workItem,
                concurrencyGate,
                executionPolicyOverride,
                cancellationToken)).ToArray();
            var batchTask = Task.WhenAll(processingTasks);
            var renewalTask = RenewHeldMessagesUntilAsync(
                heldMessages,
                heldMessagesGate,
                batchTask,
                cancellationToken);

            try
            {
                await batchTask;
            }
            catch
            {
                // Individual task exceptions are collected from the task array below.
            }

            try
            {
                await renewalTask;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || orchestratorShutdownSignal.IsShutdownRequested)
            {
                // The processing tasks carry the meaningful cancellation state; cleanup below uses non-cancelled tokens.
            }

            foreach (var processingTask in processingTasks)
            {
                if (processingTask.Status != TaskStatus.Faulted || processingTask.Exception is null)
                {
                    continue;
                }

                failures.AddRange(processingTask.Exception.Flatten().InnerExceptions);
            }
        }

        foreach (var state in messageStates)
        {
            if (state.HasFailure)
            {
                summary.IncrementFailed();
                await workflowEventReceiverService.ReleaseMessageAsync(state.Message, CancellationToken.None);
                continue;
            }

            if (!state.Handled && state.DeadLetterMessage && !state.HoldMessageForNextRun)
            {
                await workflowEventReceiverService.DeadLetterMessageAsync(
                    state.Message,
                    state.DeadLetterReason ?? NoListeningWorkflowDeadLetterReason,
                    state.DeadLetterDescription,
                    cancellationToken);
                summary.IncrementDeadLettered();
                WriteLog(
                    ("stage", "message"),
                    ("queue", queue),
                    ("decision", "deadletter"),
                    ("outcome", state.DeadLetterOutcome ?? "unmatched"),
                    ("eventId", state.EventId),
                    ("eventType", state.EventType));
                continue;
            }

            if (!state.Handled)
            {
                state.HoldMessageForNextRun = true;
            }

            if (state.HoldMessageForNextRun)
            {
                summary.IncrementHeld();
                WriteLog(
                    ("stage", "message"),
                    ("queue", queue),
                    ("decision", "hold"),
                    ("outcome", state.HoldOutcome ?? (state.ListeningWorkflowCount == 0 ? "unmatched" : "deferred")),
                    ("eventId", state.EventId),
                    ("eventType", state.EventType));

                lock (heldMessagesGate)
                {
                    heldMessages.Add(state.Message);
                }

                continue;
            }

            await workflowEventReceiverService.CompleteMessageAsync(state.Message, cancellationToken);
            summary.IncrementCompleted();
        }

        return failures;
    }

    private async Task<ProcessEventsMessageState?> BuildMessageStateAsync(
        string queue,
        WorkflowReceivedMessage message,
        ProcessEventsLogSummary summary,
        bool dryRun,
        bool diagnosticsEnabled,
        WorkflowExecutionPolicyOverride? executionPolicyOverride,
        CancellationToken cancellationToken)
    {
        if (!workflowEventReceiverService.TryGetProcessablePayload(message, out var parsedPayload))
        {
            summary.IncrementInvalid();
            if (diagnosticsEnabled)
            {
                WriteLog(
                    ("stage", "diagnostic"),
                    ("queue", queue),
                    ("decision", dryRun ? "dryrun-invalid" : "invalid"),
                    ("matched", false),
                    ("reason", "invalid_payload"));
            }

            if (dryRun)
            {
                await workflowEventReceiverService.ReleaseMessageAsync(message, cancellationToken);
                return null;
            }

            await workflowEventReceiverService.CompleteMessageAsync(message, cancellationToken);
            summary.IncrementCompleted();
            WriteLog(
                ("stage", "message"),
                ("queue", queue),
                ("decision", "complete"),
                ("outcome", "invalid"));
            return null;
        }

        var state = new ProcessEventsMessageState(message)
        {
            EventId = GetPayloadString(parsedPayload, "id"),
            EventType = GetPayloadString(parsedPayload, "type"),
            EventSource = GetPayloadString(parsedPayload, "source")
        };

        if (diagnosticsEnabled)
        {
            WriteLog(
                ("stage", "diagnostic"),
                ("queue", queue),
                ("decision", "received"),
                ("eventId", state.EventId),
                ("eventType", state.EventType),
                ("eventSource", state.EventSource));
        }

        var listeningWorkflows = await workflowEventDispatchService.ResolveListeningWorkflowsAsync(parsedPayload, cancellationToken);
        state.ListeningWorkflowCount = listeningWorkflows.Count;
        if (listeningWorkflows.Count == 0)
        {
            if (diagnosticsEnabled)
            {
                WriteLog(
                    ("stage", "diagnostic"),
                    ("queue", queue),
                    ("decision", dryRun ? "dryrun-match" : "match"),
                    ("matched", false),
                    ("reason", "no_listening_workflow"),
                    ("would", dryRun ? "deadletter" : null),
                    ("eventId", state.EventId),
                    ("eventType", state.EventType),
                    ("eventSource", state.EventSource));
            }

            if (dryRun)
            {
                await workflowEventReceiverService.ReleaseMessageAsync(message, cancellationToken);
                return null;
            }

            await workflowEventReceiverService.DeadLetterMessageAsync(
                message,
                NoListeningWorkflowDeadLetterReason,
                BuildNoListeningWorkflowDeadLetterDescription(state.EventId, state.EventType),
                cancellationToken);
            summary.IncrementDeadLettered();
            WriteLog(
                ("stage", "message"),
                ("queue", queue),
                ("decision", "deadletter"),
                ("outcome", "unmatched"),
                ("eventId", state.EventId),
                ("eventType", state.EventType));
            return null;
        }

        if (diagnosticsEnabled)
        {
            foreach (var listeningWorkflow in listeningWorkflows)
            {
                var prediction = dryRun
                    ? await PredictDryRunActionAsync(listeningWorkflow, executionPolicyOverride, cancellationToken)
                    : null;

                WriteLog(
                    ("stage", "diagnostic"),
                    ("queue", queue),
                    ("decision", dryRun ? "dryrun-match" : "match"),
                    ("matched", true),
                    ("workflowType", listeningWorkflow.WorkflowType),
                    ("bindingMode", BuildBindingModeSummary(listeningWorkflow)),
                    ("canStart", listeningWorkflow.CanStart),
                    ("canResume", listeningWorkflow.CanResume),
                    ("matchingBindingCount", listeningWorkflow.MatchingBindings.Count),
                    ("would", prediction?.Would),
                    ("reason", prediction?.Reason),
                    ("eventId", state.EventId),
                    ("eventType", state.EventType),
                    ("eventSource", state.EventSource));
            }
        }

        if (dryRun)
        {
            await workflowEventReceiverService.ReleaseMessageAsync(message, cancellationToken);
            return null;
        }

        var processedEventWorkflowKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var listeningWorkflow in listeningWorkflows)
        {
            var primaryMatch = listeningWorkflow.MatchingBindings[0];
            var eventWorkflowKey = workflowEventProcessingService.BuildEventWorkflowProcessingKey(primaryMatch.EventEnvelope, listeningWorkflow.WorkflowType);

            if (!processedEventWorkflowKeys.Add(eventWorkflowKey))
            {
                continue;
            }

            state.WorkItems.Add(new ProcessEventsWorkflowWorkItem(state, listeningWorkflow, eventWorkflowKey));
        }

        if (state.WorkItems.Count == 0)
        {
            state.HoldMessageForNextRun = true;
            state.HoldOutcome = "deferred";
        }

        return state;
    }

    private async Task<ProcessEventsDryRunPrediction> PredictDryRunActionAsync(
        ListeningWorkflowTypeContext listeningWorkflow,
        WorkflowExecutionPolicyOverride? executionPolicyOverride,
        CancellationToken cancellationToken)
    {
        var primaryMatch = listeningWorkflow.MatchingBindings[0];
        var workflowType = listeningWorkflow.WorkflowType;
        var canResume = listeningWorkflow.CanResume;
        var canStart = listeningWorkflow.CanStart;
        var workflowInstancePersistenceMode = workflowExecutionPolicyProvider.Resolve(workflowType)
            .Apply(executionPolicyOverride)
            .WorkflowInstances;
        var workflowInstancePersistenceBypassed = workflowInstancePersistenceMode != WorkflowInstancePersistenceMode.Enabled;

        if (canResume && workflowInstancePersistenceBypassed && !canStart)
        {
            return new ProcessEventsDryRunPrediction("deadletter", "no_awaiting_workflow_instance");
        }

        if (canResume && !workflowInstancePersistenceBypassed)
        {
            var correlatedStatuses = new[]
            {
                WorkflowInstanceStatuses.Running,
                WorkflowInstanceStatuses.Suspended,
                WorkflowInstanceStatuses.Failed,
                WorkflowInstanceStatuses.Completed,
                WorkflowInstanceStatuses.Cancelled
            };

            var correlatedInstances = await workflowCorrelationService.ResolveInstancesAsync(
                workflowType,
                primaryMatch.EventEnvelope.CorrelationKeys,
                correlatedStatuses,
                cancellationToken);

            var candidateWorkflowInstanceCount = correlatedInstances.WorkflowInstances
                .Count(x => !string.IsNullOrWhiteSpace(x.WorkflowInstanceId));

            if (candidateWorkflowInstanceCount > 0)
            {
                return new ProcessEventsDryRunPrediction("resume", null);
            }

            if (!canStart)
            {
                return new ProcessEventsDryRunPrediction("deadletter", "no_awaiting_workflow_instance");
            }
        }

        if (canStart)
        {
            return new ProcessEventsDryRunPrediction("start", null);
        }

        return new ProcessEventsDryRunPrediction("hold", "deferred");
    }

    private static string BuildBindingModeSummary(ListeningWorkflowTypeContext listeningWorkflow)
        => string.Join(
            ",",
            listeningWorkflow.MatchingBindings
                .Select(match => match.Binding.Mode.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(mode => mode, StringComparer.OrdinalIgnoreCase));

    private async Task ProcessWorkItemWithConcurrencyAsync(
        string queue,
        ProcessEventsWorkflowWorkItem workItem,
        SemaphoreSlim concurrencyGate,
        WorkflowExecutionPolicyOverride? executionPolicyOverride,
        CancellationToken cancellationToken)
    {
        await concurrencyGate.WaitAsync(cancellationToken);
        try
        {
            var result = await ProcessWorkflowMatchAsync(queue, workItem.MessageState.Message, workItem.ListeningWorkflow, workItem.EventWorkflowKey, executionPolicyOverride, cancellationToken);
            workItem.MessageState.Apply(result);
        }
        catch (Exception ex)
        {
            workItem.MessageState.MarkFailed();
            throw new InvalidOperationException($"{BuildMessageFailureReason(workItem)} {ex.Message}", ex);
        }
        finally
        {
            concurrencyGate.Release();
        }
    }

    private async Task<ProcessEventsWorkflowResult> ProcessWorkflowMatchAsync(
        string queue,
        WorkflowReceivedMessage message,
        ListeningWorkflowTypeContext listeningWorkflow,
        string eventWorkflowKey,
        WorkflowExecutionPolicyOverride? executionPolicyOverride,
        CancellationToken cancellationToken)
    {
        var primaryMatch = listeningWorkflow.MatchingBindings[0];
        var workflowType = listeningWorkflow.WorkflowType;
        var canResume = listeningWorkflow.CanResume;
        var canStart = listeningWorkflow.CanStart;
        var workflowInstancePersistenceMode = workflowExecutionPolicyProvider.Resolve(workflowType)
            .Apply(executionPolicyOverride)
            .WorkflowInstances;
        var workflowInstancePersistenceBypassed = workflowInstancePersistenceMode != WorkflowInstancePersistenceMode.Enabled;

        if (canResume && workflowInstancePersistenceBypassed && !canStart)
        {
            return ProcessEventsWorkflowResult.DeadLetter(
                "no_awaiting_workflow_instance",
                NoAwaitingWorkflowInstanceDeadLetterReason,
                BuildNoAwaitingWorkflowInstanceDeadLetterDescription(primaryMatch.EventEnvelope.EventId, primaryMatch.EventEnvelope.EventType, workflowType));
        }

        if (canResume && !workflowInstancePersistenceBypassed)
        {
            var resumableStatuses = new[]
            {
                WorkflowInstanceStatuses.Running,
                WorkflowInstanceStatuses.Suspended,
                WorkflowInstanceStatuses.Failed
            };

            var correlatedStatuses = new[]
            {
                WorkflowInstanceStatuses.Running,
                WorkflowInstanceStatuses.Suspended,
                WorkflowInstanceStatuses.Failed,
                WorkflowInstanceStatuses.Completed,
                WorkflowInstanceStatuses.Cancelled
            };

            var correlatedInstances = await workflowCorrelationService.ResolveInstancesAsync(
                workflowType,
                primaryMatch.EventEnvelope.CorrelationKeys,
                correlatedStatuses,
                cancellationToken);

            var candidateWorkflowInstanceIds = correlatedInstances.WorkflowInstances
                .Where(x => !string.IsNullOrWhiteSpace(x.WorkflowInstanceId))
                .Select(x => x.WorkflowInstanceId!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (candidateWorkflowInstanceIds.Count == 0 && !canStart)
            {
                return ProcessEventsWorkflowResult.DeadLetter(
                    "no_awaiting_workflow_instance",
                    NoAwaitingWorkflowInstanceDeadLetterReason,
                    BuildNoAwaitingWorkflowInstanceDeadLetterDescription(primaryMatch.EventEnvelope.EventId, primaryMatch.EventEnvelope.EventType, workflowType));
            }

            if (candidateWorkflowInstanceIds.Count > 0)
            {
                var workflowInstancesToProcess = new List<WorkflowInstance>();
                var suppressStartForExistingInstance = false;
                var handled = false;
                var holdMessageForNextRun = false;

                foreach (var correlatedWorkflowId in candidateWorkflowInstanceIds)
                {
                    var getWorkflowInstanceResult = await workflowInstanceStore.GetAsync(correlatedWorkflowId, cancellationToken);
                    if (!getWorkflowInstanceResult.Success || getWorkflowInstanceResult.NotFound || getWorkflowInstanceResult.WorkflowInstance is null)
                    {
                        throw new InvalidOperationException(getWorkflowInstanceResult.FailureReason ?? $"Failed to load workflow instance '{correlatedWorkflowId}'.");
                    }

                    workflowInstancesToProcess.Add(getWorkflowInstanceResult.WorkflowInstance);
                }

                foreach (var workflowInstance in workflowInstancesToProcess)
                {
                    var workflowInstanceId = workflowInstance.Id;
                    var resumeBinding = workflowCorrelationService.SelectResumeBindingMatch(listeningWorkflow.MatchingBindings, workflowInstance);
                    var resumeEnvelope = resumeBinding.Envelope;
                    var correlationLink = workflowCorrelationService.FindCorrelationLink(workflowInstance, resumeEnvelope);
                    var deduplicationKeys = workflowEventProcessingService.BuildDeduplicationKeys(resumeEnvelope, workflowInstanceId);

                    if (!resumableStatuses.Contains(workflowInstance.Status, StringComparer.OrdinalIgnoreCase))
                    {
                        var terminalDuplicate = workflowEventProcessingService.HasAlreadyProcessedEvent(workflowInstance, deduplicationKeys, workflowInstance.Status)
                            || (string.Equals(workflowInstance.OriginatingEventId, resumeEnvelope.EventId, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(workflowInstance.OriginatingEventType, resumeEnvelope.EventType, StringComparison.OrdinalIgnoreCase));

                        if (terminalDuplicate
                            && (string.Equals(workflowInstance.Status, WorkflowInstanceStatuses.Completed, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(workflowInstance.Status, WorkflowInstanceStatuses.Cancelled, StringComparison.OrdinalIgnoreCase)))
                        {
                            handled = true;
                            ProcessEventsLogSummary.Current.IncrementTerminalDuplicate();
                            TerminalWorkflowObservationCounter.Add(1,
                                KeyValuePair.Create<string, object?>("workflowType", workflowType),
                                KeyValuePair.Create<string, object?>("workflowInstanceId", workflowInstanceId),
                                KeyValuePair.Create<string, object?>("status", workflowInstance.Status));

                            await workflowExecutionTraceSink.RecordAsync(workflowInstanceId, workflowType, "process.resume.skipped_terminal_duplicate", new JsonObject
                            {
                                ["eventId"] = resumeEnvelope.EventId,
                                ["eventType"] = resumeEnvelope.EventType,
                                ["status"] = workflowInstance.Status
                            }, cancellationToken);
                            WriteLog(
                                ("stage", "message"),
                                ("queue", queue),
                                ("decision", "skip"),
                                ("outcome", "terminal_duplicate"),
                                ("eventId", resumeEnvelope.EventId),
                                ("eventType", resumeEnvelope.EventType),
                                ("workflowType", workflowType),
                                ("workflowInstanceId", workflowInstanceId));

                            suppressStartForExistingInstance = true;
                        }

                        continue;
                    }

                    suppressStartForExistingInstance = true;

                    if (!workflowEventProcessingService.IsEventCurrentForCorrelation(correlationLink, resumeEnvelope))
                    {
                        handled = true;
                        ProcessEventsLogSummary.Current.IncrementObserved();
                        ProcessEventsLogSummary.Current.IncrementStale();
                        await workflowExecutionTraceSink.RecordAsync(workflowInstanceId, workflowType, "process.resume.skipped_stale_event", new JsonObject
                        {
                            ["eventId"] = resumeEnvelope.EventId,
                            ["eventType"] = resumeEnvelope.EventType,
                            ["correlationKeys"] = BuildCorrelationKeysJson(resumeEnvelope.CorrelationKeys)
                        }, cancellationToken);
                        WriteLog(
                            ("stage", "message"),
                            ("queue", queue),
                            ("decision", "observe"),
                            ("outcome", "stale"),
                            ("eventId", resumeEnvelope.EventId),
                            ("eventType", resumeEnvelope.EventType),
                            ("workflowType", workflowType),
                            ("workflowInstanceId", workflowInstanceId));

                        var suspendedObservationResult = await workflowInstanceMutationService.UpsertAsync(
                            workflowInstanceId,
                            (current, utcNow) =>
                            {
                                if (current is null)
                                {
                                    throw new InvalidOperationException($"Failed to load workflow instance '{workflowInstanceId}' while recording a stale event observation.");
                                }

                                var currentCorrelationLink = workflowCorrelationService.FindCorrelationLink(current, resumeEnvelope);
                                workflowEventProcessingService.MarkProcessed(current, currentCorrelationLink, resumeEnvelope, deduplicationKeys,
                                    utcNow, workflowEventProcessingService.ActionObserved, current.Status);
                                current.LastUpdated = utcNow;
                                return ValueTask.FromResult(WorkflowInstanceMutationCommand.Write(current));
                            },
                            cancellationToken);

                        if (!suspendedObservationResult.Success || suspendedObservationResult.WorkflowInstance is null)
                        {
                            throw new InvalidOperationException(suspendedObservationResult.FailureReason ?? $"Failed to persist workflow instance '{workflowInstanceId}' after observing event.");
                        }

                        await workflowExecutionTraceSink.WriteWorkflowInstanceSnapshotAsync(suspendedObservationResult.WorkflowInstance, cancellationToken);
                        continue;
                    }

                    if (workflowEventProcessingService.HasAlreadyProcessedEvent(workflowInstance, deduplicationKeys, workflowInstance.Status))
                    {
                        handled = true;
                        ProcessEventsLogSummary.Current.IncrementDeduplicated();
                        await workflowExecutionTraceSink.RecordAsync(workflowInstanceId, workflowType, "process.resume.skipped_deduplicated", new JsonObject
                        {
                            ["eventId"] = resumeEnvelope.EventId,
                            ["eventType"] = resumeEnvelope.EventType
                        }, cancellationToken);
                        WriteLog(
                            ("stage", "message"),
                            ("queue", queue),
                            ("decision", "skip"),
                            ("outcome", "deduplicated"),
                            ("eventId", resumeEnvelope.EventId),
                            ("eventType", resumeEnvelope.EventType),
                            ("workflowType", workflowType),
                            ("workflowInstanceId", workflowInstanceId));
                        continue;
                    }

                    await workflowExecutionTraceSink.RecordAsync(workflowInstanceId, workflowType, "process.resume.selected", new JsonObject
                    {
                        ["eventId"] = resumeEnvelope.EventId,
                        ["eventType"] = resumeEnvelope.EventType,
                        ["correlationKeys"] = BuildCorrelationKeysJson(resumeEnvelope.CorrelationKeys),
                        ["canStart"] = canStart,
                        ["canResume"] = canResume
                    }, cancellationToken);
                    WriteLog(
                        ("stage", "message"),
                        ("queue", queue),
                        ("decision", "resume"),
                        ("outcome", "selected"),
                        ("eventId", resumeEnvelope.EventId),
                        ("eventType", resumeEnvelope.EventType),
                        ("workflowType", workflowType),
                        ("workflowInstanceId", workflowInstanceId));

                    var (resumeResponse, resumeError) = await RunWithActiveMessageRenewalAsync(
                        message,
                        ct => mediator.TrySend(new ResumeWorkflowRequest
                        {
                            WorkflowType = workflowType,
                            WorkflowInstanceId = workflowInstanceId,
                            ExecutionPolicyOverride = executionPolicyOverride,
                            Input = new JsonObject
                            {
                                ["__initiatingEvent"] = new JsonObject
                                {
                                    [nameof(WorkflowEvent.EventId)] = resumeEnvelope.EventId,
                                    [nameof(WorkflowEvent.EventType)] = resumeEnvelope.EventType,
                                    [nameof(WorkflowEvent.Payload)] = resumeEnvelope.NormalizedPayload.DeepClone()
                                }
                            }
                        }, ct),
                        cancellationToken);

                    var authoredResumeFailure = resumeError is null
                        && string.Equals(resumeResponse?.Status, WorkflowInstanceStatuses.Failed, StringComparison.OrdinalIgnoreCase);

                    if (resumeResponse is null || resumeError is not null || (!resumeResponse.Success && !authoredResumeFailure))
                    {
                        if (resumeError is null && string.Equals(resumeResponse?.FailureReason, ResumeWorkflowRequestHandler.WorkflowInstanceLockedFailureReason, StringComparison.OrdinalIgnoreCase))
                        {
                            holdMessageForNextRun = true;
                            await workflowExecutionTraceSink.RecordAsync(workflowInstanceId, workflowType, "process.resume.locked", new JsonObject
                            {
                                ["eventId"] = resumeEnvelope.EventId,
                                ["eventType"] = resumeEnvelope.EventType
                            }, cancellationToken);
                            WriteLog(
                                ("stage", "message"),
                                ("queue", queue),
                                ("decision", "hold"),
                                ("outcome", "resume_locked"),
                                ("eventId", resumeEnvelope.EventId),
                                ("eventType", resumeEnvelope.EventType),
                                ("workflowType", workflowType),
                                ("workflowInstanceId", workflowInstanceId));
                            break;
                        }

                        if (resumeError is null && string.Equals(resumeResponse?.FailureReason, ResumeWorkflowRequestHandler.NonResumableListenStepFailureReason, StringComparison.OrdinalIgnoreCase))
                        {
                            handled = true;
                            ProcessEventsLogSummary.Current.IncrementObserved();
                            var nonResumableObservationResult = await workflowInstanceMutationService.UpsertAsync(
                                workflowInstanceId,
                                (current, utcNow) =>
                                {
                                    if (current is null)
                                    {
                                        throw new InvalidOperationException($"Failed to load workflow instance '{workflowInstanceId}' while recording a non-resumable event observation.");
                                    }

                                    var currentCorrelationLink = workflowCorrelationService.FindCorrelationLink(current, resumeEnvelope);
                                    workflowEventProcessingService.MarkProcessed(current, currentCorrelationLink, resumeEnvelope, deduplicationKeys,
                                        utcNow, workflowEventProcessingService.ActionObserved, current.Status);
                                    current.LastUpdated = utcNow;
                                    return ValueTask.FromResult(WorkflowInstanceMutationCommand.Write(current));
                                },
                                cancellationToken);
                            if (!nonResumableObservationResult.Success || nonResumableObservationResult.WorkflowInstance is null)
                            {
                                throw new InvalidOperationException(nonResumableObservationResult.FailureReason ?? $"Failed to persist workflow instance '{workflowInstanceId}' after non-resumable observation.");
                            }

                            await workflowExecutionTraceSink.WriteWorkflowInstanceSnapshotAsync(nonResumableObservationResult.WorkflowInstance, cancellationToken);
                            await workflowExecutionTraceSink.RecordAsync(workflowInstanceId, workflowType, "process.resume.non_resumable_observed", new JsonObject
                            {
                                ["eventId"] = resumeEnvelope.EventId,
                                ["eventType"] = resumeEnvelope.EventType
                            }, cancellationToken);
                            WriteLog(
                                ("stage", "message"),
                                ("queue", queue),
                                ("decision", "observe"),
                                ("outcome", "non_resumable"),
                                ("eventId", resumeEnvelope.EventId),
                                ("eventType", resumeEnvelope.EventType),
                                ("workflowType", workflowType),
                                ("workflowInstanceId", workflowInstanceId));
                            continue;
                        }

                        throw resumeError ?? new InvalidOperationException(resumeResponse?.FailureReason ?? $"Failed to resume workflow '{workflowType}'.");
                    }

                    if (authoredResumeFailure)
                    {
                        throw new InvalidOperationException(resumeResponse?.FailureReason ?? $"Workflow '{workflowType}' failed while resuming.");
                    }

                    var getUpdatedInstanceResult = await workflowInstanceStore.GetAsync(workflowInstanceId, cancellationToken);
                    if (!getUpdatedInstanceResult.Success || getUpdatedInstanceResult.NotFound || getUpdatedInstanceResult.WorkflowInstance is null)
                    {
                        throw new InvalidOperationException(getUpdatedInstanceResult.FailureReason ?? $"Failed to reload workflow instance '{workflowInstanceId}' after resume.");
                    }

                    handled = true;

                    var resumeMutationResult = await workflowInstanceMutationService.UpsertAsync(
                        workflowInstanceId,
                        (current, utcNow) =>
                        {
                            if (current is null)
                            {
                                throw new InvalidOperationException($"Failed to load workflow instance '{workflowInstanceId}' after resume.");
                            }

                            var currentCorrelationLink = workflowCorrelationService.FindCorrelationLink(current, resumeEnvelope);
                            workflowEventProcessingService.MarkProcessed(current, currentCorrelationLink, resumeEnvelope, deduplicationKeys,
                                utcNow, workflowEventProcessingService.ActionResume, current.Status);

                            var matchedUpdatedAwaitingDescriptors = workflowCorrelationService.MatchAwaitingEventDescriptors(current, resumeEnvelope);
                            if (matchedUpdatedAwaitingDescriptors.Count > 0)
                            {
                                workflowCorrelationService.RemoveMatchedAwaitingDescriptors(current, matchedUpdatedAwaitingDescriptors);
                            }

                            current.LastUpdated = utcNow;
                            return ValueTask.FromResult(WorkflowInstanceMutationCommand.Write(current));
                        },
                        cancellationToken);
                    if (!resumeMutationResult.Success || resumeMutationResult.WorkflowInstance is null)
                    {
                        throw new InvalidOperationException(resumeMutationResult.FailureReason ?? $"Failed to persist workflow instance '{workflowInstanceId}'.");
                    }

                    var updatedWorkflowInstance = resumeMutationResult.WorkflowInstance;
                    await workflowExecutionTraceSink.WriteWorkflowInstanceSnapshotAsync(updatedWorkflowInstance, cancellationToken);
                    await workflowExecutionTraceSink.RecordAsync(workflowInstanceId, workflowType, "process.resume.completed", new JsonObject
                    {
                        ["eventId"] = resumeEnvelope.EventId,
                        ["eventType"] = resumeEnvelope.EventType,
                        ["status"] = updatedWorkflowInstance.Status
                    }, cancellationToken);
                    ProcessEventsLogSummary.Current.IncrementResumed();
                    WriteLog(
                        ("stage", "message"),
                        ("queue", queue),
                        ("decision", "resume"),
                        ("outcome", "completed"),
                        ("eventId", resumeEnvelope.EventId),
                        ("eventType", resumeEnvelope.EventType),
                        ("workflowType", workflowType),
                        ("workflowInstanceId", workflowInstanceId));
                }

                if (holdMessageForNextRun)
                {
                    return ProcessEventsWorkflowResult.Hold("resume_locked", handled);
                }

                if (suppressStartForExistingInstance)
                {
                    return ProcessEventsWorkflowResult.FromHandled(handled);
                }
            }
        }

        if (!canStart)
        {
            return ProcessEventsWorkflowResult.None;
        }

        var reservedWorkflowInstanceId = workflowInstancePersistenceMode == WorkflowInstancePersistenceMode.Disabled
            ? null
            : BuildReservedWorkflowInstanceId(eventWorkflowKey);

        if (!workflowInstancePersistenceBypassed)
        {
            var startReservationUtcNow = DateTimeOffset.UtcNow;
            var startReservation = new WorkflowInstance
            {
                Id = reservedWorkflowInstanceId!,
                WorkflowInstanceId = reservedWorkflowInstanceId,
                WorkflowType = workflowType,
                Status = WorkflowInstanceStatuses.Starting,
                CorrelationKeys = primaryMatch.EventEnvelope.CorrelationKeys
                    .Select(key => new WorkflowResolvedCorrelationKey { Name = key.Name, Value = key.Value })
                    .ToList(),
                OriginatingEventId = primaryMatch.EventEnvelope.EventId,
                OriginatingEventType = primaryMatch.EventEnvelope.EventType,
                OriginatingEventSource = primaryMatch.EventEnvelope.EventSource,
                CreatedOn = startReservationUtcNow,
                LastUpdated = startReservationUtcNow
            };

            var startReservationResult = await workflowInstanceMutationService.CreateAsync(
                startReservation,
                cancellationToken,
                lockKey: $"start::{eventWorkflowKey}");

            if (!startReservationResult.Success)
            {
                if (startReservationResult.Conflict)
                {
                    var existingReservationResult = await workflowInstanceStore.GetAsync(reservedWorkflowInstanceId!, cancellationToken);
                    if (!existingReservationResult.Success)
                    {
                        throw new InvalidOperationException(existingReservationResult.FailureReason ?? $"Failed to load start reservation '{reservedWorkflowInstanceId}'.");
                    }

                    if (!existingReservationResult.NotFound && existingReservationResult.WorkflowInstance is not null)
                    {
                        ProcessEventsLogSummary.Current.IncrementDeduplicated();
                        await workflowExecutionTraceSink.RecordAsync(reservedWorkflowInstanceId!, workflowType, "process.start.skipped_duplicate_claim", new JsonObject
                        {
                            ["eventId"] = primaryMatch.EventEnvelope.EventId,
                            ["eventType"] = primaryMatch.EventEnvelope.EventType,
                            ["status"] = existingReservationResult.WorkflowInstance.Status
                        }, cancellationToken);
                        WriteLog(
                            ("stage", "message"),
                            ("queue", queue),
                            ("decision", "skip"),
                            ("outcome", "start_claimed"),
                            ("eventId", primaryMatch.EventEnvelope.EventId),
                            ("eventType", primaryMatch.EventEnvelope.EventType),
                            ("workflowType", workflowType),
                            ("workflowInstanceId", reservedWorkflowInstanceId));
                        return ProcessEventsWorkflowResult.FromHandled(true);
                    }
                }

                throw new InvalidOperationException(startReservationResult.FailureReason ?? $"Failed to create start reservation for workflow '{workflowType}'.");
            }
        }

        var input = listeningWorkflow.StartInput?.DeepClone() as JsonObject ?? new JsonObject();
        input["__initiatingEvent"] = new JsonObject
        {
            [nameof(WorkflowEvent.EventId)] = primaryMatch.EventEnvelope.EventId,
            [nameof(WorkflowEvent.EventType)] = primaryMatch.EventEnvelope.EventType,
            [nameof(WorkflowEvent.Payload)] = primaryMatch.EventEnvelope.NormalizedPayload.DeepClone(),
            ["EventSource"] = primaryMatch.EventEnvelope.EventSource
        };

        var (startResponse, startError) = await RunWithActiveMessageRenewalAsync(
            message,
            ct => mediator.TrySend(new StartWorkflowRequest
            {
                WorkflowType = workflowType,
                Input = input,
                WorkflowInstanceId = reservedWorkflowInstanceId,
                ExecutionPolicyOverride = executionPolicyOverride
            }, ct),
            cancellationToken);

        var authoredStartFailure = startError is null
            && string.Equals(startResponse?.Status, WorkflowInstanceStatuses.Failed, StringComparison.OrdinalIgnoreCase);

        if (startResponse is null || startError is not null || (!startResponse.Success && !authoredStartFailure))
        {
            throw startError ?? new InvalidOperationException(startResponse?.FailureReason ?? $"Failed to start workflow '{workflowType}'.");
        }

        if (authoredStartFailure)
        {
            throw new InvalidOperationException(startResponse?.FailureReason ?? $"Workflow '{workflowType}' failed while starting.");
        }

        if (!string.IsNullOrWhiteSpace(startResponse.WorkflowInstanceId))
        {
            await workflowExecutionTraceSink.RecordAsync(startResponse.WorkflowInstanceId, workflowType, "process.start.selected", new JsonObject
            {
                ["eventId"] = primaryMatch.EventEnvelope.EventId,
                ["eventType"] = primaryMatch.EventEnvelope.EventType,
                ["correlationKeys"] = BuildCorrelationKeysJson(primaryMatch.EventEnvelope.CorrelationKeys),
                ["matchingBindingCount"] = listeningWorkflow.MatchingBindings.Count
            }, cancellationToken);
            WriteLog(
                ("stage", "message"),
                ("queue", queue),
                ("decision", "start"),
                ("outcome", "selected"),
                ("eventId", primaryMatch.EventEnvelope.EventId),
                ("eventType", primaryMatch.EventEnvelope.EventType),
                ("workflowType", workflowType),
                ("workflowInstanceId", startResponse.WorkflowInstanceId));
        }

        if (string.IsNullOrWhiteSpace(startResponse.WorkflowInstanceId))
        {
            return ProcessEventsWorkflowResult.FromHandled(true);
        }

        if (workflowInstancePersistenceBypassed)
        {
            await workflowExecutionTraceSink.RecordAsync(startResponse.WorkflowInstanceId, workflowType, "process.start.completed", new JsonObject
            {
                ["eventId"] = primaryMatch.EventEnvelope.EventId,
                ["eventType"] = primaryMatch.EventEnvelope.EventType,
                ["status"] = startResponse.Status,
                ["workflowInstances"] = workflowInstancePersistenceMode.ToString()
            }, cancellationToken);
            ProcessEventsLogSummary.Current.IncrementStarted();
            WriteLog(
                ("stage", "message"),
                ("queue", queue),
                ("decision", "start"),
                ("outcome", "completed"),
                ("eventId", primaryMatch.EventEnvelope.EventId),
                ("eventType", primaryMatch.EventEnvelope.EventType),
                ("workflowType", workflowType),
                ("workflowInstanceId", startResponse.WorkflowInstanceId));
            return ProcessEventsWorkflowResult.FromHandled(true);
        }

        await workflowCorrelationService.EnsureWorkflowCorrelationAsync(startResponse.WorkflowInstanceId, primaryMatch.EventEnvelope, cancellationToken);
        var startDeduplicationKeys = workflowEventProcessingService.BuildDeduplicationKeys(primaryMatch.EventEnvelope, startResponse.WorkflowInstanceId);

        var startedInstanceMutationResult = await workflowInstanceMutationService.UpsertAsync(
            startResponse.WorkflowInstanceId,
            (current, utcNow) =>
            {
                if (current is null)
                {
                    throw new InvalidOperationException($"Failed to load workflow instance '{startResponse.WorkflowInstanceId}' after start.");
                }

                var startedCorrelationLink = workflowCorrelationService.FindCorrelationLink(current, primaryMatch.EventEnvelope);
                workflowEventProcessingService.MarkProcessed(current, startedCorrelationLink, primaryMatch.EventEnvelope, startDeduplicationKeys,
                    utcNow, workflowEventProcessingService.ActionStart, current.Status);
                current.LastUpdated = utcNow;
                return ValueTask.FromResult(WorkflowInstanceMutationCommand.Write(current));
            },
            cancellationToken);
        if (!startedInstanceMutationResult.Success || startedInstanceMutationResult.WorkflowInstance is null)
        {
            throw new InvalidOperationException(startedInstanceMutationResult.FailureReason ?? $"Failed to persist workflow instance '{startResponse.WorkflowInstanceId}' after start event processing.");
        }

        var startedInstance = startedInstanceMutationResult.WorkflowInstance;
        await workflowExecutionTraceSink.WriteWorkflowInstanceSnapshotAsync(startedInstance, cancellationToken);
        await workflowExecutionTraceSink.RecordAsync(startResponse.WorkflowInstanceId, workflowType, "process.start.completed", new JsonObject
        {
            ["eventId"] = primaryMatch.EventEnvelope.EventId,
            ["eventType"] = primaryMatch.EventEnvelope.EventType,
            ["status"] = startedInstance.Status
        }, cancellationToken);
        ProcessEventsLogSummary.Current.IncrementStarted();
        WriteLog(
            ("stage", "message"),
            ("queue", queue),
            ("decision", "start"),
            ("outcome", "completed"),
            ("eventId", primaryMatch.EventEnvelope.EventId),
            ("eventType", primaryMatch.EventEnvelope.EventType),
            ("workflowType", workflowType),
            ("workflowInstanceId", startResponse.WorkflowInstanceId));

        return ProcessEventsWorkflowResult.FromHandled(true);
    }

    private static JsonArray BuildCorrelationKeysJson(IReadOnlyCollection<WorkflowResolvedCorrelationKey> correlationKeys)
    {
        return new JsonArray(correlationKeys.Select(key => new JsonObject
        {
            ["name"] = key.Name,
            ["value"] = key.Value
        }).ToArray());
    }

    private static string BuildReservedWorkflowInstanceId(string eventWorkflowKey)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(eventWorkflowKey));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private async Task<DateTimeOffset> RenewHeldMessagesIfDueAsync(
        List<WorkflowReceivedMessage> heldMessages,
        object heldMessagesGate,
        DateTimeOffset lastRenewalUtc,
        bool force,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now - lastRenewalUtc < HeldMessageRenewalInterval)
        {
            return lastRenewalUtc;
        }

        await RenewHeldMessagesAsync(heldMessages, heldMessagesGate, cancellationToken);
        return now;
    }

    private async Task RenewHeldMessagesUntilAsync(
        List<WorkflowReceivedMessage> heldMessages,
        object heldMessagesGate,
        Task untilTask,
        CancellationToken cancellationToken)
    {
        while (!untilTask.IsCompleted)
        {
            var delayTask = Task.Delay(HeldMessageRenewalInterval, cancellationToken);
            var completedTask = await Task.WhenAny(untilTask, delayTask);
            if (completedTask == untilTask)
            {
                break;
            }

            await RenewHeldMessagesAsync(heldMessages, heldMessagesGate, cancellationToken);
        }
    }

    private async Task RenewHeldMessagesAsync(
        List<WorkflowReceivedMessage> heldMessages,
        object heldMessagesGate,
        CancellationToken cancellationToken)
    {
        WorkflowReceivedMessage[] snapshot;
        lock (heldMessagesGate)
        {
            snapshot = heldMessages.ToArray();
        }

        foreach (var heldMessage in snapshot)
        {
            await workflowEventReceiverService.RenewMessageLockAsync(heldMessage, cancellationToken);
        }
    }

    private static async Task ReleaseHeldMessagesAsync(
        IWorkflowEventReceiverService workflowEventReceiverService,
        List<WorkflowReceivedMessage>? heldMessages,
        object heldMessagesGate,
        CancellationToken cancellationToken)
    {
        if (heldMessages is null)
        {
            return;
        }

        WorkflowReceivedMessage[] snapshot;
        lock (heldMessagesGate)
        {
            snapshot = heldMessages.ToArray();
            heldMessages.Clear();
        }

        if (snapshot.Length == 0)
        {
            return;
        }

        List<Exception>? releaseErrors = null;

        foreach (var heldMessage in snapshot)
        {
            try
            {
                await workflowEventReceiverService.ReleaseMessageAsync(heldMessage, cancellationToken);
            }
            catch (Exception ex)
            {
                releaseErrors ??= [];
                releaseErrors.Add(ex);
            }
        }

        if (releaseErrors is { Count: > 0 })
        {
            throw new AggregateException("Failed to release one or more held event messages.", releaseErrors);
        }
    }

    private async Task<T> RunWithActiveMessageRenewalAsync<T>(
        WorkflowReceivedMessage message,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var operationTask = operation(cancellationToken);

        while (true)
        {
            var delayTask = Task.Delay(HeldMessageRenewalInterval, cancellationToken);
            var completedTask = await Task.WhenAny(operationTask, delayTask);
            if (completedTask == operationTask)
            {
                return await operationTask;
            }

            await workflowEventReceiverService.RenewMessageLockAsync(message, cancellationToken);
        }
    }

    private static string? GetPayloadString(JsonObject payload, string propertyName)
    {
        if (!payload.TryGetPropertyValue(propertyName, out var node) || node is null)
        {
            return null;
        }

        return node is JsonValue value && value.TryGetValue<string>(out var textValue)
            ? textValue
            : node.ToJsonString();
    }

    private static string BuildFailureReason(IReadOnlyCollection<Exception> failures)
    {
        if (failures.Count == 1)
        {
            return failures.First().Message;
        }

        return $"Failed to process {failures.Count} event message(s): {string.Join("; ", failures.Select(x => x.Message).Distinct(StringComparer.Ordinal))}";
    }

    private static string BuildNoListeningWorkflowDeadLetterDescription(string? eventId, string? eventType)
    {
        if (!string.IsNullOrWhiteSpace(eventId) && !string.IsNullOrWhiteSpace(eventType))
        {
            return $"No listening workflow matched event id '{eventId}' of type '{eventType}'.";
        }

        if (!string.IsNullOrWhiteSpace(eventId))
        {
            return $"No listening workflow matched event id '{eventId}'.";
        }

        if (!string.IsNullOrWhiteSpace(eventType))
        {
            return $"No listening workflow matched event type '{eventType}'.";
        }

        return "No listening workflow matched the event.";
    }

    private static string BuildNoAwaitingWorkflowInstanceDeadLetterDescription(string? eventId, string? eventType, string workflowType)
    {
        if (!string.IsNullOrWhiteSpace(eventId) && !string.IsNullOrWhiteSpace(eventType))
        {
            return $"No awaiting workflow instance matched event id '{eventId}' of type '{eventType}' for workflow '{workflowType}'.";
        }

        if (!string.IsNullOrWhiteSpace(eventId))
        {
            return $"No awaiting workflow instance matched event id '{eventId}' for workflow '{workflowType}'.";
        }

        if (!string.IsNullOrWhiteSpace(eventType))
        {
            return $"No awaiting workflow instance matched event type '{eventType}' for workflow '{workflowType}'.";
        }

        return $"No awaiting workflow instance matched the event for workflow '{workflowType}'.";
    }

    private static string BuildMessageFailureReason(ProcessEventsWorkflowWorkItem workItem)
    {
        var primaryMatch = workItem.ListeningWorkflow.MatchingBindings[0];
        return $"Failed to process event '{primaryMatch.EventEnvelope.EventId}' for workflow '{workItem.ListeningWorkflow.WorkflowType}'.";
    }

    private static void WriteLog(params (string Key, object? Value)[] fields)
    {
        var fragments = new List<string>(fields.Length);

        foreach (var (key, value) in fields)
        {
            if (value is null)
            {
                continue;
            }

            fragments.Add($"{key}={FormatValue(value)}");
        }

        Console.WriteLine($"ProcessEvents: {string.Join(" ", fragments)}");
    }

    private static string FormatValue(object value)
    {
        var text = value switch
        {
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        } ?? string.Empty;

        text = text.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

        if (text.Length == 0)
        {
            return "\"\"";
        }

        return text.IndexOfAny([' ', '=']) >= 0
            ? $"\"{text.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : text;
    }

    private sealed record ProcessEventsSettings(bool Success, string? QueueName, int MaxConcurrency, string? FailureReason)
    {
        public static ProcessEventsSettings Ok(string queueName, int maxConcurrency) => new(true, queueName, maxConcurrency, null);
        public static ProcessEventsSettings Fail(string failureReason) => new(false, null, 0, failureReason);
    }

    private sealed record ProcessEventsWorkflowWorkItem(ProcessEventsMessageState MessageState, ListeningWorkflowTypeContext ListeningWorkflow, string EventWorkflowKey);

    private sealed record ProcessEventsDryRunPrediction(string Would, string? Reason);

    private sealed record ProcessEventsWorkflowResult(
        bool Handled,
        bool HoldMessageForNextRun,
        string? HoldOutcome,
        bool DeadLetterMessage,
        string? DeadLetterOutcome,
        string? DeadLetterReason,
        string? DeadLetterDescription)
    {
        public static readonly ProcessEventsWorkflowResult None = new(false, false, null, false, null, null, null);
        public static ProcessEventsWorkflowResult FromHandled(bool handled) => new(handled, false, null, false, null, null, null);
        public static ProcessEventsWorkflowResult Hold(string holdOutcome, bool handled) => new(handled, true, holdOutcome, false, null, null, null);
        public static ProcessEventsWorkflowResult DeadLetter(string deadLetterOutcome, string deadLetterReason, string deadLetterDescription) =>
            new(false, false, null, true, deadLetterOutcome, deadLetterReason, deadLetterDescription);
    }

    private sealed class ProcessEventsMessageState(WorkflowReceivedMessage message)
    {
        private readonly object _gate = new();

        public WorkflowReceivedMessage Message { get; } = message;
        public string? EventId { get; init; }
        public string? EventType { get; init; }
        public string? EventSource { get; init; }
        public int ListeningWorkflowCount { get; set; }
        public List<ProcessEventsWorkflowWorkItem> WorkItems { get; } = [];
        public bool Handled { get; private set; }
        public bool HoldMessageForNextRun { get; set; }
        public string? HoldOutcome { get; set; }
        public bool DeadLetterMessage { get; private set; }
        public string? DeadLetterOutcome { get; private set; }
        public string? DeadLetterReason { get; private set; }
        public string? DeadLetterDescription { get; private set; }
        public bool HasFailure { get; private set; }

        public void Apply(ProcessEventsWorkflowResult result)
        {
            lock (_gate)
            {
                Handled |= result.Handled;
                HoldMessageForNextRun |= result.HoldMessageForNextRun;
                HoldOutcome ??= result.HoldOutcome;
                DeadLetterMessage |= result.DeadLetterMessage;
                DeadLetterOutcome ??= result.DeadLetterOutcome;
                DeadLetterReason ??= result.DeadLetterReason;
                DeadLetterDescription ??= result.DeadLetterDescription;
            }
        }

        public void MarkFailed()
        {
            lock (_gate)
            {
                HasFailure = true;
            }
        }
    }

    private sealed class ProcessEventsLogSummary
    {
        private static readonly AsyncLocal<ProcessEventsLogSummary?> CurrentSummary = new();
        private int _received;
        private int _completed;
        private int _held;
        private int _deadLettered;
        private int _failed;
        private int _started;
        private int _resumed;
        private int _observed;
        private int _invalid;
        private int _deduplicated;
        private int _stale;
        private int _terminalDuplicate;

        public static ProcessEventsLogSummary Current => CurrentSummary.Value ?? throw new InvalidOperationException("Process events summary context is not available.");
        public int Received => _received;
        public int Completed => _completed;
        public int Held => _held;
        public int DeadLettered => _deadLettered;
        public int Failed => _failed;
        public int Started => _started;
        public int Resumed => _resumed;
        public int Observed => _observed;
        public int Invalid => _invalid;
        public int Deduplicated => _deduplicated;
        public int Stale => _stale;
        public int TerminalDuplicate => _terminalDuplicate;

        public IDisposable BeginScope()
        {
            var previous = CurrentSummary.Value;
            CurrentSummary.Value = this;
            return new ScopeToken(() => CurrentSummary.Value = previous);
        }

        public void AddReceived(int count) => Interlocked.Add(ref _received, count);
        public void IncrementCompleted() => Interlocked.Increment(ref _completed);
        public void IncrementHeld() => Interlocked.Increment(ref _held);
        public void IncrementDeadLettered() => Interlocked.Increment(ref _deadLettered);
        public void IncrementFailed() => Interlocked.Increment(ref _failed);
        public void IncrementStarted() => Interlocked.Increment(ref _started);
        public void IncrementResumed() => Interlocked.Increment(ref _resumed);
        public void IncrementObserved() => Interlocked.Increment(ref _observed);
        public void IncrementInvalid() => Interlocked.Increment(ref _invalid);
        public void IncrementDeduplicated() => Interlocked.Increment(ref _deduplicated);
        public void IncrementStale() => Interlocked.Increment(ref _stale);
        public void IncrementTerminalDuplicate() => Interlocked.Increment(ref _terminalDuplicate);

        private sealed class ScopeToken(Action onDispose) : IDisposable
        {
            private readonly Action _onDispose = onDispose;
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _onDispose();
                }
            }
        }
    }
}
