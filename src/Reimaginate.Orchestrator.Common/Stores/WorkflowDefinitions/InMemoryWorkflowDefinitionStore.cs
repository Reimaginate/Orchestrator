using System.Collections.Concurrent;
using System.Text;
using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.Common.Stores.WorkflowDefinitions.Parsing;

namespace Reimaginate.Orchestrator.Common.Stores.WorkflowDefinitions;

public sealed class InMemoryWorkflowDefinitionStore : IWorkflowDefinitionStore
{
    private readonly ConcurrentDictionary<string, string> _definitions;

    public InMemoryWorkflowDefinitionStore(IEnumerable<KeyValuePair<string, string>>? definitions = null)
    {
        _definitions = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (definitions is null)
        {
            return;
        }

        foreach (var (workflowType, definition) in definitions)
        {
            _definitions[NormalizeWorkflowType(workflowType)] = definition;
        }
    }

    public bool SupportsEventTriggerBindingQuery => true;

    public Task<Result<string>> OpenReadAsync(string workflowType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedWorkflowType = NormalizeWorkflowType(workflowType);
        if (_definitions.TryGetValue(normalizedWorkflowType, out var definition))
        {
            return Task.FromResult(new Result<string> { Success = true, Data = definition });
        }

        return Task.FromResult(new Result<string>
        {
            Success = false,
            FailureReason = $"Workflow definition '{normalizedWorkflowType}' was not found in memory."
        });
    }

    public Task<Result<bool>> ExistsAsync(string workflowType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var exists = _definitions.ContainsKey(NormalizeWorkflowType(workflowType));
        return Task.FromResult(new Result<bool> { Success = true, Data = exists });
    }

    public async IAsyncEnumerable<string> ListAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var workflowType in _definitions.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return workflowType;
            await Task.Yield();
        }
    }

    public Task<IReadOnlyList<WorkflowEventTriggerBindingDescriptor>> FindBindingsByEventTypesAsync(
        IReadOnlyCollection<string> eventTypes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (eventTypes.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<WorkflowEventTriggerBindingDescriptor>>([]);
        }

        var requestedEventTypes = new HashSet<string>(eventTypes.Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
        if (requestedEventTypes.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<WorkflowEventTriggerBindingDescriptor>>([]);
        }

        var matches = new List<WorkflowEventTriggerBindingDescriptor>();

        foreach (var (workflowType, definition) in _definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(definition));
            var bindings = WorkflowEventTriggerBindingDescriptorParser.Parse(stream, workflowType);
            matches.AddRange(bindings.Where(x => requestedEventTypes.Contains(x.EventType)));
        }

        return Task.FromResult<IReadOnlyList<WorkflowEventTriggerBindingDescriptor>>(matches);
    }

    private static string NormalizeWorkflowType(string workflowType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowType);

        return workflowType.EndsWith(".workflow.yaml", StringComparison.OrdinalIgnoreCase)
            ? workflowType
            : $"{workflowType}.workflow.yaml";
    }
}
