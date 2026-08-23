using System.Text.Json.Nodes;
using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.Common.Services.WorkflowEventNormalization;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowEventReceivers;

public sealed class WorkflowEventReceiverService(
    IWorkflowEventReceiver eventReceiver,
    IWorkflowEventNormalizationService workflowEventNormalizationService) : IWorkflowEventReceiverService
{
    private readonly IWorkflowEventReceiver _eventReceiver = eventReceiver;
    private readonly IWorkflowEventNormalizationService _workflowEventNormalizationService = workflowEventNormalizationService;

    public Task<IReadOnlyList<WorkflowReceivedMessage>> ReceiveMessagesAsync(string queueName, int maxMessages, TimeSpan maxWaitTime, CancellationToken cancellationToken)
    {
        return _eventReceiver.ReceiveMessagesAsync(queueName, maxMessages, maxWaitTime, cancellationToken);
    }

    public Task CompleteMessageAsync(WorkflowReceivedMessage message, CancellationToken cancellationToken)
    {
        return _eventReceiver.CompleteMessageAsync(message, cancellationToken);
    }

    public Task ReleaseMessageAsync(WorkflowReceivedMessage message, CancellationToken cancellationToken)
    {
        return _eventReceiver.ReleaseMessageAsync(message, cancellationToken);
    }

    public Task DeadLetterMessageAsync(WorkflowReceivedMessage message, string reason, string? description, CancellationToken cancellationToken)
    {
        return _eventReceiver.DeadLetterMessageAsync(message, reason, description, cancellationToken);
    }

    public Task RenewMessageLockAsync(WorkflowReceivedMessage message, CancellationToken cancellationToken)
    {
        return _eventReceiver.RenewMessageLockAsync(message, cancellationToken);
    }

    public bool TryGetProcessablePayload(WorkflowReceivedMessage message, out JsonObject payload)
    {
        payload = null!;

        if (message.Payload is not JsonObject parsedPayload)
        {
            return false;
        }

        var clonedPayload = (JsonObject)parsedPayload.DeepClone();
        if (clonedPayload["data"] is JsonValue dataValue
            && dataValue.TryGetValue<string>(out var textData)
            && string.IsNullOrWhiteSpace(textData))
        {
            return false;
        }

        payload = clonedPayload;
        return true;
    }
}
