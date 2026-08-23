using Reimaginate.Mediator;
using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.Common.Requests.Internal.GetWorkflowInstance;

public class GetWorkflowInstanceRequestHandler(IWorkflowInstanceStore workflowInstanceStore) : IHandler<GetWorkflowInstanceRequest, GetWorkflowInstanceResponse>
{
    public async Task<GetWorkflowInstanceResponse> HandleAsync(GetWorkflowInstanceRequest request, CancellationToken cancellationToken)
    {
        var getResult = await workflowInstanceStore.GetAsync(request.WorkflowInstanceId, cancellationToken);
        if (!getResult.Success)
        {
            return new GetWorkflowInstanceResponse
            {
                Success = false,
                FailureReason = getResult.FailureReason
            };
        }

        if (getResult.NotFound || getResult.WorkflowInstance == null)
        {
            return new GetWorkflowInstanceResponse
            {
                Success = false,
                FailureReason = "Workflow instance was not found."
            };
        }

        return new GetWorkflowInstanceResponse
        {
            Success = true,
            WorkflowInstance = getResult.WorkflowInstance
        };
    }
}
