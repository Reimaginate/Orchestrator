using Reimaginate.Mediator;
using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.Common.Services.WorkflowExecution;

namespace Reimaginate.Orchestrator.Common.Requests.Internal.PersistWorkflowInstance;

public class PersistWorkflowInstanceRequest : IRequest<PersistWorkflowInstanceResponse>
{
    public string WorkflowInstanceId { get; set; } = null!;
    public string WorkflowType { get; set; } = null!;
    public string Status { get; set; } = null!;
    public string? FailureReason { get; set; }
    public WorkflowExecutionFailureDetails? FailureDetails { get; set; }
    public DateTimeOffset? FailedOn { get; set; }
    public string? CurrentCheckpointId { get; set; }
    public string? CheckpointRunId { get; set; }
    public List<WorkflowCorrelationLink>? Correlations { get; set; }
    public List<WorkflowAwaitingEventDescriptor>? AwaitingEvents { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
    public string? OriginatingEventId { get; set; }
    public string? OriginatingEventType { get; set; }
    public string? OriginatingEventSource { get; set; }
    public WorkflowExecutionPolicyOverride? ExecutionPolicyOverride { get; set; }
}
