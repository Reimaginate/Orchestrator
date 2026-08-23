using Reimaginate.Mediator;
using Reimaginate.Orchestrator.Common.Services.WorkflowCorrelation;

namespace Reimaginate.Orchestrator.Common.Requests.Internal.RegisterWorkflowCorrelation;

public class RegisterWorkflowCorrelationRequestHandler(IWorkflowCorrelationService workflowCorrelationService) : IHandler<RegisterWorkflowCorrelationRequest, RegisterWorkflowCorrelationResponse>
{
    public async Task<RegisterWorkflowCorrelationResponse> HandleAsync(RegisterWorkflowCorrelationRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.WorkflowInstanceId))
        {
            return Failure("WorkflowInstanceId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.WorkflowType))
        {
            return Failure("WorkflowType is required.");
        }

        if (string.IsNullOrWhiteSpace(request.EntityType))
        {
            return Failure("EntityType is required.");
        }

        if (string.IsNullOrWhiteSpace(request.EntityId))
        {
            return Failure("EntityId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.CorrelationRole))
        {
            return Failure("CorrelationRole is required.");
        }

        try
        {
            var added = await workflowCorrelationService.RegisterWorkflowCorrelationAsync(
                request.WorkflowInstanceId,
                request.WorkflowType,
                request.EntityType,
                request.EntityId,
                request.CorrelationRole,
                cancellationToken);

            return new RegisterWorkflowCorrelationResponse
            {
                Success = true,
                CorrelationAdded = added
            };
        }
        catch (Exception ex)
        {
            return Failure(ex.Message);
        }
    }

    private static RegisterWorkflowCorrelationResponse Failure(string failureReason) => new()
    {
        Success = false,
        CorrelationAdded = false,
        FailureReason = failureReason
    };
}
