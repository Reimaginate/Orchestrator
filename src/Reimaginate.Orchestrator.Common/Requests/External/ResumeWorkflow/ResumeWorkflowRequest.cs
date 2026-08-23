using System.Text.Json.Nodes;
using Reimaginate.Mediator;
using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.Common.Services.WorkflowExecution;

namespace Reimaginate.Orchestrator.Common.Requests.External.ResumeWorkflow;

[WorkflowAction("ResumeWorkflow")]
public class ResumeWorkflowRequest : IRequest<ResumeWorkflowResult>
{
    public string WorkflowType { get; set; } = null!;
    public string WorkflowInstanceId { get; set; } = null!;
    public JsonObject? Input { get; set; }
    public WorkflowExecutionPolicyOverride? ExecutionPolicyOverride { get; set; }
}
