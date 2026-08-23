using Reimaginate.Mediator;
using Reimaginate.Orchestrator.Common.Services.WorkflowExecution;

namespace Reimaginate.Orchestrator.Common.Requests.External.ProcessEvents;

public class ProcessEventsRequest : IRequest<ProcessEventsResponse>
{
    public string? QueueName { get; init; }
    public int? MaxConcurrency { get; init; }
    public WorkflowExecutionPolicyOverride? ExecutionPolicyOverride { get; init; }
    public bool DryRun { get; init; }
    public ProcessEventsDiagnosticsMode DiagnosticsMode { get; init; } = ProcessEventsDiagnosticsMode.None;
}
