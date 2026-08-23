namespace Reimaginate.Orchestrator.Abstractions;

public interface IWorkflowEventReceiver : IAsyncDisposable
{
    Task<IReadOnlyList<WorkflowReceivedMessage>> ReceiveMessagesAsync(string queueName, int maxMessages, TimeSpan maxWaitTime, CancellationToken cancellationToken);

    Task CompleteMessageAsync(WorkflowReceivedMessage message, CancellationToken cancellationToken);

    Task ReleaseMessageAsync(WorkflowReceivedMessage message, CancellationToken cancellationToken);

    Task DeadLetterMessageAsync(WorkflowReceivedMessage message, string reason, string? description, CancellationToken cancellationToken);

    Task RenewMessageLockAsync(WorkflowReceivedMessage message, CancellationToken cancellationToken);
}
