using Reimaginate.Mediator;

namespace Reimaginate.Orchestrator.Common.Requests.Internal.ResolveAndLoadWorkflowDefinition;

public class ResolveAndLoadWorkflowDefinitionRequest : IRequest<ResolveAndLoadWorkflowDefinitionResponse>
{
    public string WorkflowType { get; set; } = null!;
}
