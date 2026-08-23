using Reimaginate.Mediator;
using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.Common.Requests.Internal.RegisterWorkflowCorrelation;

[WorkflowAction("RegisterWorkflowCorrelation")]
public class RegisterWorkflowCorrelationRequest : IRequest<RegisterWorkflowCorrelationResponse>
{
    public string WorkflowInstanceId { get; set; } = null!;
    public string WorkflowType { get; set; } = null!;
    public string EntityType { get; set; } = null!;
    public string EntityId { get; set; } = null!;
    public string? TenantId { get; set; }
    public string? WorkspaceId { get; set; }
    public string CorrelationRole { get; set; } = null!;
}
