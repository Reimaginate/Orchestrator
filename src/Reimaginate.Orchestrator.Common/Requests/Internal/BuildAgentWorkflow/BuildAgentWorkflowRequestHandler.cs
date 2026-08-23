using Reimaginate.Mediator;
using Reimaginate.Orchestrator.Common.Services.WorkflowDefinitions;

namespace Reimaginate.Orchestrator.Common.Requests.Internal.BuildAgentWorkflow;

public class BuildAgentWorkflowRequestHandler(IWorkflowDefinitionService workflowDefinitionService)
    : IHandler<BuildAgentWorkflowRequest, BuildAgentWorkflowResponse>
{
    public async Task<BuildAgentWorkflowResponse> HandleAsync(BuildAgentWorkflowRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Ast);
        ArgumentNullException.ThrowIfNull(request.Definition);

        var workflow = await workflowDefinitionService.BuildAgentWorkflowAsync(request.WorkflowType, request.Ast, request.Definition, cancellationToken);

        return new BuildAgentWorkflowResponse
        {
            Success = true,
            Workflow = workflow.Workflow,
            DiagnosticsGraph = workflow.DiagnosticsGraph,
            Definition = workflow.Definition
        };
    }
}
