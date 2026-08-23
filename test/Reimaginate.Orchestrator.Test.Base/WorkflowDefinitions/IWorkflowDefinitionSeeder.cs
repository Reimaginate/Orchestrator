namespace Reimaginate.Orchestrator.Test.Base.WorkflowDefinitions;

public interface IWorkflowDefinitionSeeder
{
    void Clear();
    Task SeedAsync(string workflowType, string yamlDefinition, CancellationToken cancellationToken = default);
    Task SeedManyAsync(IReadOnlyDictionary<string, string> definitions, CancellationToken cancellationToken = default);
}
