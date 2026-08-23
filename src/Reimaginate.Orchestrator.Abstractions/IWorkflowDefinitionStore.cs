namespace Reimaginate.Orchestrator.Abstractions;

public interface IWorkflowDefinitionStore
{
    Task<Result<string>> OpenReadAsync(string workflowType, CancellationToken cancellationToken);
    Task<Result<bool>> ExistsAsync(string workflowType, CancellationToken cancellationToken);
    IAsyncEnumerable<string> ListAsync(CancellationToken cancellationToken);

    bool SupportsEventTriggerBindingQuery => false;

    Task<IReadOnlyList<WorkflowEventTriggerBindingDescriptor>> FindBindingsByEventTypesAsync(
        IReadOnlyCollection<string> eventTypes,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("Event trigger binding query is not supported by this workflow definition store.");
}
