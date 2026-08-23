using System.Collections.Concurrent;
using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.Test.Base.Mocks;

public sealed class MockWorkflowEventEmitter : IWorkflowEventEmitter
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<WorkflowEventMessage>> _messages = new(StringComparer.OrdinalIgnoreCase);

    public Task<WorkflowEventMessage> EmitAsync(string destination, WorkflowEventMessage message, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(message);

        var queue = _messages.GetOrAdd(destination, _ => new ConcurrentQueue<WorkflowEventMessage>());
        queue.Enqueue(message);

        return Task.FromResult(message);
    }

    public IReadOnlyList<WorkflowEventMessage> DequeueAll(string destination)
    {
        if (!_messages.TryGetValue(destination, out var queue))
        {
            return [];
        }

        var messages = new List<WorkflowEventMessage>();
        while (queue.TryDequeue(out var message))
        {
            messages.Add(message);
        }

        return messages;
    }

    public void Clear()
    {
        _messages.Clear();
    }
}
