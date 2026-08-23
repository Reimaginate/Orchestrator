using System.Text.Json.Nodes;
using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowEventReceivers;

public interface IWorkflowEventReceiverService
{
    Task<IReadOnlyList<WorkflowReceivedMessage>> ReceiveMessagesAsync(string queueName, int maxMessages, TimeSpan maxWaitTime, CancellationToken cancellationToken);

    Task CompleteMessageAsync(WorkflowReceivedMessage message, CancellationToken cancellationToken);

    Task ReleaseMessageAsync(WorkflowReceivedMessage message, CancellationToken cancellationToken);

    Task DeadLetterMessageAsync(WorkflowReceivedMessage message, string reason, string? description, CancellationToken cancellationToken);

    Task RenewMessageLockAsync(WorkflowReceivedMessage message, CancellationToken cancellationToken);

    bool TryGetProcessablePayload(WorkflowReceivedMessage message, out JsonObject payload);
    
}
