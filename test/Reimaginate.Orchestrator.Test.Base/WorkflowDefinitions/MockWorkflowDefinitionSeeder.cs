using Reimaginate.Orchestrator.Test.Base.Mocks;

namespace Reimaginate.Orchestrator.Test.Base.WorkflowDefinitions;

public sealed class MockWorkflowDefinitionSeeder(MockWorkflowDefinitionStore workflowDefinitionStore) : IWorkflowDefinitionSeeder
{
    private readonly MockWorkflowDefinitionStore _workflowDefinitionStore = workflowDefinitionStore ?? throw new ArgumentNullException(nameof(workflowDefinitionStore));

    public void Clear()
    {
        _workflowDefinitionStore.Clear();
    }

    public Task SeedAsync(string workflowType, string yamlDefinition, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _workflowDefinitionStore.Set(workflowType, yamlDefinition);
        return Task.CompletedTask;
    }

    public async Task SeedManyAsync(IReadOnlyDictionary<string, string> definitions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        foreach (var (workflowType, yamlDefinition) in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SeedAsync(workflowType, yamlDefinition, cancellationToken);
        }
    }
}
