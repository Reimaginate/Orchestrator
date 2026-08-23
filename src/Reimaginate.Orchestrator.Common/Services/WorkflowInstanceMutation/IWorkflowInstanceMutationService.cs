using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowInstanceMutation;

public interface IWorkflowInstanceMutationService
{
    Task<WorkflowInstanceMutationResult> CreateAsync(WorkflowInstance workflowInstance, CancellationToken cancellationToken, string? lockKey = null);
    Task<WorkflowInstanceMutationResult> UpsertAsync(string workflowInstanceId, Func<WorkflowInstance?, DateTimeOffset, ValueTask<WorkflowInstanceMutationCommand>> mutator, CancellationToken cancellationToken, string? lockKey = null);
}
