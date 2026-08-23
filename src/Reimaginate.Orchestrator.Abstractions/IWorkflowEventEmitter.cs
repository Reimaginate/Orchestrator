namespace Reimaginate.Orchestrator.Abstractions;

public interface IWorkflowEventEmitter
{
    Task<WorkflowEventMessage> EmitAsync(string destination, WorkflowEventMessage message, CancellationToken cancellationToken);
}
