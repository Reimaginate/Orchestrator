using Reimaginate.Orchestrator.Common.Models;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowEventBindings;

public interface IWorkflowEventBindingService
{
    IReadOnlyDictionary<string, IReadOnlyList<WorkflowEventBinding>> ResolveWorkflowEventMap();
    IReadOnlyDictionary<string, IReadOnlyList<WorkflowEventBinding>> ResolveWorkflowEventMap(string workflowsDirectory);
    IReadOnlyList<WorkflowEventBinding> ResolveListeningWorkflowEventBindings(IReadOnlyDictionary<string, IReadOnlyList<WorkflowEventBinding>> workflowEventMap, IEnumerable<string> eventTypes);
    Task<IReadOnlyList<WorkflowEventBinding>> ResolveListeningWorkflowEventBindingsAsync(IEnumerable<string> eventTypes, CancellationToken cancellationToken);
}
