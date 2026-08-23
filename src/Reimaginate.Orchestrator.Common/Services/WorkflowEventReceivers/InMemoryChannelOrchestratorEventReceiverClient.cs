using System.Collections.Concurrent;
using System.Threading.Channels;
using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowEventReceivers;

public sealed class InMemoryChannelOrchestratorEventReceiverClient
{
    private readonly ConcurrentDictionary<string, Channel<WorkflowTransportMessage>> _queueChannels = new(StringComparer.OrdinalIgnoreCase);

    internal Channel<WorkflowTransportMessage> GetOrCreateQueueChannel(string queueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        return _queueChannels.GetOrAdd(queueName, _ => Channel.CreateUnbounded<WorkflowTransportMessage>());
    }

    public ValueTask EnqueueAsync(string queueName, WorkflowTransportMessage message, CancellationToken cancellationToken = default)
    {
        var channel = GetOrCreateQueueChannel(queueName);
        return channel.Writer.WriteAsync(message, cancellationToken);
    }

    public int ClearQueue(string queueName)
    {
        var removedMessages = 0;

        if (!_queueChannels.TryGetValue(queueName, out var channel))
        {
            return removedMessages;
        }

        while (channel.Reader.TryRead(out _))
        {
            removedMessages++;
        }

        return removedMessages;
    }

    public int ClearQueues()
    {
        var removedMessages = 0;

        foreach (var queueChannel in _queueChannels.Values)
        {
            while (queueChannel.Reader.TryRead(out _))
            {
                removedMessages++;
            }
        }

        return removedMessages;
    }
}
