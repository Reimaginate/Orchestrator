using Microsoft.Agents.AI.Workflows;
using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.DSL;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowDefinitions;

public interface IWorkflowDefinitionService
{
    Task<WorkflowBuildArtifact> BuildAgentWorkflowAsync(string workflowType, WorkflowAst ast, WorkflowDefinition definition, CancellationToken cancellationToken);
    List<WorkflowAwaitingEventDescriptor> BuildAwaitingEventDescriptors(WorkflowDefinition definition, string? checkpointId);
    List<WorkflowAwaitingEventDescriptor> BuildAwaitingEventDescriptors(WorkflowDefinition definition, string? checkpointId, IReadOnlyCollection<string> activeTaskNames);
}
