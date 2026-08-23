using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.Common.Models;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowCorrelation;

public interface IWorkflowCorrelationService
{
    IReadOnlyList<WorkflowResolvedCorrelationKey> ResolveCorrelationKeys(WorkflowEventCorrelationBinding? correlationBinding, ProcessEventEnvelope envelope);
    Task<WorkflowCorrelationResolution> ResolveRunningInstancesAsync(string workflowType, IReadOnlyList<WorkflowResolvedCorrelationKey> correlationKeys, CancellationToken cancellationToken);
    Task<WorkflowCorrelationResolution> ResolveInstancesAsync(string workflowType, IReadOnlyList<WorkflowResolvedCorrelationKey> correlationKeys, IReadOnlyCollection<string> statuses, CancellationToken cancellationToken);
    Task EnsureWorkflowCorrelationAsync(string workflowInstanceId, ProcessEventEnvelope normalizedEvent, CancellationToken cancellationToken);
    WorkflowCorrelationLink? FindCorrelationLink(WorkflowInstance workflowInstance, ProcessEventEnvelope envelope);
    IReadOnlyList<WorkflowAwaitingEventDescriptor> MatchAwaitingEventDescriptors(WorkflowInstance workflowInstance, ProcessEventEnvelope envelope);
    void RemoveMatchedAwaitingDescriptors(WorkflowInstance instance, IReadOnlyCollection<WorkflowAwaitingEventDescriptor> matchedDescriptors);
    (WorkflowEventBinding Binding, ProcessEventEnvelope Envelope) SelectResumeBindingMatch(IReadOnlyList<(WorkflowEventBinding Binding, ProcessEventEnvelope Envelope)> matches, WorkflowInstance instance);
    Task<bool> RegisterWorkflowCorrelationAsync(string workflowInstanceId, string workflowType, string entityType, string entityId, string correlationRole, CancellationToken cancellationToken);
    
}
