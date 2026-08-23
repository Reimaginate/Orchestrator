using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.Common.Stores.WorkflowDefinitions;

public sealed class FallbackWorkflowDefinitionStore(
    IWorkflowDefinitionStore primaryStore,
    IWorkflowDefinitionStore fallbackStore) : IWorkflowDefinitionStore
{
    private readonly IWorkflowDefinitionStore primaryStore = primaryStore ?? throw new ArgumentNullException(nameof(primaryStore));
    private readonly IWorkflowDefinitionStore fallbackStore = fallbackStore ?? throw new ArgumentNullException(nameof(fallbackStore));

    public bool SupportsEventTriggerBindingQuery => primaryStore.SupportsEventTriggerBindingQuery && fallbackStore.SupportsEventTriggerBindingQuery;

    public async Task<Result<string>> OpenReadAsync(string workflowType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var primaryExists = await primaryStore.ExistsAsync(workflowType, cancellationToken);
        if (!primaryExists.Success)
        {
            return new Result<string>
            {
                Success = false,
                FailureReason = primaryExists.FailureReason
            };
        }

        if (primaryExists.Data)
        {
            return await primaryStore.OpenReadAsync(workflowType, cancellationToken);
        }

        return await fallbackStore.OpenReadAsync(workflowType, cancellationToken);
    }

    public async Task<Result<bool>> ExistsAsync(string workflowType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var primaryExists = await primaryStore.ExistsAsync(workflowType, cancellationToken);
        if (!primaryExists.Success)
        {
            return new Result<bool>
            {
                Success = false,
                FailureReason = primaryExists.FailureReason
            };
        }

        if (primaryExists.Data)
        {
            return primaryExists;
        }

        return await fallbackStore.ExistsAsync(workflowType, cancellationToken);
    }

    public async IAsyncEnumerable<string> ListAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await foreach (var workflowType in primaryStore.ListAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (seen.Add(workflowType))
            {
                yield return workflowType;
            }
        }

        await foreach (var workflowType in fallbackStore.ListAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (seen.Add(workflowType))
            {
                yield return workflowType;
            }
        }
    }

    public async Task<IReadOnlyList<WorkflowEventTriggerBindingDescriptor>> FindBindingsByEventTypesAsync(
        IReadOnlyCollection<string> eventTypes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!SupportsEventTriggerBindingQuery)
        {
            throw new NotSupportedException("Event trigger binding query is not supported by the configured workflow definition stores.");
        }

        var primaryBindings = await primaryStore.FindBindingsByEventTypesAsync(eventTypes, cancellationToken);
        var fallbackBindings = await fallbackStore.FindBindingsByEventTypesAsync(eventTypes, cancellationToken);

        return primaryBindings
            .Concat(fallbackBindings)
            .GroupBy(CreateBindingKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static string CreateBindingKey(WorkflowEventTriggerBindingDescriptor binding)
        => $"{binding.EventType}|{binding.WorkflowType}|{binding.Filter}|{binding.EventTypePath}|{binding.Mode}";
}
