using Reimaginate.Mediator;
using Reimaginate.Orchestrator.DSL;

namespace Reimaginate.Orchestrator.Common.Requests.Internal.BuildAgentWorkflow;

public class BuildAgentWorkflowRequest : IRequest<BuildAgentWorkflowResponse>
{
    public string WorkflowType { get; set; } = string.Empty;

    public WorkflowAst Ast { get; set; } = null!;

    public WorkflowDefinition Definition { get; set; } = null!;
}
