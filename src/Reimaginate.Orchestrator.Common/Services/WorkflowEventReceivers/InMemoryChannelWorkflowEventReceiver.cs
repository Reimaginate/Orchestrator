using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.Common.Services.WorkflowEventNormalization;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowEventReceivers;

public sealed class InMemoryChannelWorkflowEventReceiver(
    InMemoryChannelOrchestratorEventReceiverClient queueClient,
    IWorkflowEventMessageNormalizationService workflowEventMessageNormalizationService,
    IWorkflowEventAuditSink? workflowEventAuditSink = null,
    TimeSpan? lockDuration = null,
    int maxDeliveryCount = 10) : IWorkflowEventReceiver
{
    #region Dependencies and configuration

    private readonly InMemoryChannelOrchestratorEventReceiverClient _queueClient = queueClient ?? throw new ArgumentNullException(nameof(queueClient));
    private readonly IWorkflowEventMessageNormalizationService _workflowEventMessageNormalizationService = workflowEventMessageNormalizationService ?? throw new ArgumentNullException(nameof(workflowEventMessageNormalizationService));
    private readonly ConcurrentDictionary<string, QueueState> _queueStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly IWorkflowEventAuditSink? _workflowEventAuditSink = workflowEventAuditSink;

    public TimeSpan LockDuration { get; } = lockDuration ?? TimeSpan.FromSeconds(30);
    public int MaxDeliveryCount { get; } = maxDeliveryCount;

    #endregion

    #region Internal state models

    private sealed record LockState(DateTimeOffset LockedUntilUtc, int DeliveryCount);

    private sealed class QueueState(ChannelReader<WorkflowTransportMessage> reader)
    {
        public ChannelReader<WorkflowTransportMessage> Reader { get; } = reader;
        public ConcurrentQueue<WorkflowTransportMessage> Available { get; } = new();
        public ConcurrentDictionary<WorkflowTransportMessage, LockState> InFlight { get; } = new();
        public ConcurrentDictionary<string, WorkflowTransportMessage> ReceiptLookup { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    #endregion

    #region Message receive and completion

    public async Task<IReadOnlyList<WorkflowReceivedMessage>> ReceiveMessagesAsync(
        string queueName,
        int maxMessages,
        TimeSpan maxWaitTime,
        CancellationToken cancellationToken)
    {
        // Pull messages from an in-memory queue while respecting visibility lock semantics.
        var messages = new List<WorkflowReceivedMessage>(Math.Max(1, maxMessages));
        if (maxMessages <= 0) return messages;

        var state = GetOrCreateQueueState(queueName);
        var deadline = DateTimeOffset.UtcNow + maxWaitTime;

        while (messages.Count < maxMessages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            RequeueExpiredLocks(state);
            DrainAvailableChannelItems(state);

            while (messages.Count < maxMessages && state.Available.TryDequeue(out var visible))
            {
                // A message is only returned if we can move it into the in-flight lock registry.
                if (TryLock(state, visible))
                {
                    var workflowMessage = _workflowEventMessageNormalizationService.Normalize(
                        visible.Body,
                        visible.MessageId,
                        visible.ApplicationProperties);
                    var payload = _workflowEventMessageNormalizationService.ToCloudEventPayload(workflowMessage);
                    _workflowEventAuditSink?.RecordReceivedEvent((JsonObject)payload.DeepClone());
                    var receiptHandle = Guid.NewGuid().ToString("N");
                    state.ReceiptLookup[receiptHandle] = visible;
                    messages.Add(new WorkflowReceivedMessage
                    {
                        ReceiptHandle = receiptHandle,
                        Payload = payload
                    });
                }
            }

            if (messages.Count > 0)
            {
                break;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var waitTask = state.Reader.WaitToReadAsync(cancellationToken).AsTask();
            var timeoutTask = Task.Delay(remaining, cancellationToken);

            var completed = await Task.WhenAny(waitTask, timeoutTask).ConfigureAwait(false);
            if (completed == timeoutTask)
            {
                break;
            }

            if (!await waitTask.ConfigureAwait(false))
            {
                continue;
            }

            DrainAvailableChannelItems(state);
        }

        return messages;
    }

    public Task CompleteMessageAsync(WorkflowReceivedMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (message is null)
        {
            return Task.CompletedTask;
        }

        if (!TryResolveTrackedMessage(message, out var queueState, out var transportMessage))
        {
            return Task.CompletedTask;
        }

        queueState.ReceiptLookup.TryRemove(message.ReceiptHandle, out _);
        queueState.InFlight.TryRemove(transportMessage, out _);

        return Task.CompletedTask;
    }

    public Task ReleaseMessageAsync(WorkflowReceivedMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (message is null)
        {
            return Task.CompletedTask;
        }

        if (!TryResolveTrackedMessage(message, out var queueState, out var transportMessage))
        {
            return Task.CompletedTask;
        }

        queueState.ReceiptLookup.TryRemove(message.ReceiptHandle, out _);
        queueState.InFlight.TryRemove(transportMessage, out _);
        queueState.Available.Enqueue(transportMessage);

        return Task.CompletedTask;
    }

    public Task DeadLetterMessageAsync(WorkflowReceivedMessage message, string reason, string? description, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (message is null)
        {
            return Task.CompletedTask;
        }

        if (!TryResolveTrackedMessage(message, out var queueState, out var transportMessage))
        {
            return Task.CompletedTask;
        }

        queueState.ReceiptLookup.TryRemove(message.ReceiptHandle, out _);
        queueState.InFlight.TryRemove(transportMessage, out _);

        return Task.CompletedTask;
    }

    public Task RenewMessageLockAsync(WorkflowReceivedMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (message is null)
        {
            return Task.CompletedTask;
        }

        if (!TryResolveTrackedMessage(message, out var queueState, out var transportMessage))
        {
            return Task.CompletedTask;
        }

        if (!queueState.InFlight.TryGetValue(transportMessage, out var lockState))
        {
            return Task.CompletedTask;
        }

        var renewedLockState = lockState with
        {
            LockedUntilUtc = DateTimeOffset.UtcNow + LockDuration
        };

        if (!queueState.InFlight.TryUpdate(transportMessage, renewedLockState, lockState))
        {
            throw new InvalidOperationException("Failed to renew the in-memory event lock.");
        }

        return Task.CompletedTask;
    }

    #endregion

    #region Lifecycle

    public ValueTask DisposeAsync()
    {
        _queueStates.Clear();
        return ValueTask.CompletedTask;
    }

    #endregion

    #region Queue and lock management helpers

    private QueueState GetOrCreateQueueState(string queueName)
    {
        var channel = _queueClient.GetOrCreateQueueChannel(queueName);
        return _queueStates.GetOrAdd(queueName, _ => new QueueState(channel.Reader));
    }

    private bool TryResolveTrackedMessage(WorkflowReceivedMessage message, out QueueState queueState, out WorkflowTransportMessage transportMessage)
    {
        foreach (var state in _queueStates.Values)
        {
            if (!state.ReceiptLookup.TryGetValue(message.ReceiptHandle, out var trackedTransportMessage))
            {
                continue;
            }

            queueState = state;
            transportMessage = trackedTransportMessage;
            return true;
        }

        queueState = null!;
        transportMessage = null!;
        return false;
    }

    private void RequeueExpiredLocks(QueueState queueState)
    {
        var now = DateTimeOffset.UtcNow;

        // Expired locks are made visible again until their delivery count threshold is reached.
        foreach (var kvp in queueState.InFlight)
        {
            if (kvp.Value.LockedUntilUtc > now)
            {
                continue;
            }

            if (!queueState.InFlight.TryRemove(kvp.Key, out var state))
            {
                continue;
            }

            if (state.DeliveryCount >= MaxDeliveryCount)
            {
                continue;
            }

            queueState.Available.Enqueue(kvp.Key);
        }
    }

    private static void DrainAvailableChannelItems(QueueState queueState)
    {
        // Receive calls with a zero timeout should still see messages already buffered in the channel.
        while (queueState.Reader.TryRead(out var item))
        {
            queueState.Available.Enqueue(item);
        }
    }

    private bool TryLock(QueueState queueState, WorkflowTransportMessage message)
    {
        // Existing entries get lock-renew style updates and increment delivery count.
        if (queueState.InFlight.TryGetValue(message, out var currentState))
        {
            var nextState = currentState with
            {
                LockedUntilUtc = DateTimeOffset.UtcNow + LockDuration,
                DeliveryCount = currentState.DeliveryCount + 1
            };

            return queueState.InFlight.TryUpdate(message, nextState, currentState);
        }

        var initialState = new LockState(
            LockedUntilUtc: DateTimeOffset.UtcNow + LockDuration,
            DeliveryCount: 1);

        return queueState.InFlight.TryAdd(message, initialState);
    }

    #endregion
}
