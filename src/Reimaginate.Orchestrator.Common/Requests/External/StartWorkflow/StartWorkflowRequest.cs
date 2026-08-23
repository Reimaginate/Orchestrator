using System.Text.Json.Nodes;
using Reimaginate.Mediator;
using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.Common.Services.WorkflowExecution;

namespace Reimaginate.Orchestrator.Common.Requests.External.StartWorkflow;

[WorkflowAction("StartWorkflow")]
public class StartWorkflowRequest : IRequest<StartWorkflowResult>
{
    public string? WorkflowInstanceId { get; set; }
    public string WorkflowType { get; set; } = null!;
    public JsonObject? Input { get; set; }
    public WorkflowExecutionPolicyOverride? ExecutionPolicyOverride { get; set; }
}
