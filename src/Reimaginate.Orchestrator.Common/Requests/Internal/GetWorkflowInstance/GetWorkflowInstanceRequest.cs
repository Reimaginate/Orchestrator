using Reimaginate.Mediator;

namespace Reimaginate.Orchestrator.Common.Requests.Internal.GetWorkflowInstance;

public class GetWorkflowInstanceRequest : IRequest<GetWorkflowInstanceResponse>
{
    public string WorkflowInstanceId { get; set; } = null!;
}
