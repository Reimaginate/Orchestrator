using System.Text.Json.Nodes;
using Reimaginate.Orchestrator.Common.Models;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowEventDispatch;

public interface IWorkflowEventDispatchService
{
    IReadOnlyList<ListeningWorkflowTypeContext> ResolveListeningWorkflows(IReadOnlyDictionary<string, IReadOnlyList<WorkflowEventBinding>> workflowEventMap, JsonObject payload);
    Task<IReadOnlyList<ListeningWorkflowTypeContext>> ResolveListeningWorkflowsAsync(JsonObject payload, CancellationToken cancellationToken);
}