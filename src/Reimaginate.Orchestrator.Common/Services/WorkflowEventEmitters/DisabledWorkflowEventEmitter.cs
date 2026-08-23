using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowEventEmitters;

public sealed class DisabledWorkflowEventEmitter(string reason) : IWorkflowEventEmitter
{
    public Task<WorkflowEventMessage> EmitAsync(string destination, WorkflowEventMessage message, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException(reason);
    }
}
