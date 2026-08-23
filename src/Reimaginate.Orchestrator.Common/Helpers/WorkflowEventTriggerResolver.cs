using Reimaginate.Orchestrator.Common.Models;
using Reimaginate.Orchestrator.Common.Services.WorkflowEventBindings;

namespace Reimaginate.Orchestrator.Common.Helpers;

public static class WorkflowEventTriggerResolver
{
    private static readonly IWorkflowEventBindingService BindingService = new WorkflowEventBindingService();

    public static IReadOnlyList<WorkflowEventBinding> ResolveListeningWorkflowEventBindings(
        IReadOnlyDictionary<string, IReadOnlyList<WorkflowEventBinding>> workflowEventMap,
        IEnumerable<string> eventTypes)
    {
        return BindingService.ResolveListeningWorkflowEventBindings(workflowEventMap, eventTypes);
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<WorkflowEventBinding>> ResolveWorkflowEventMap()
    {
        return BindingService.ResolveWorkflowEventMap();
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<WorkflowEventBinding>> ResolveWorkflowEventMap(string workflowsDirectory)
    {
        return BindingService.ResolveWorkflowEventMap(workflowsDirectory);
    }
}
