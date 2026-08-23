using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.Common.Requests.Internal.GetWorkflowInstance;

public class GetWorkflowInstanceResponse : Result
{
    public WorkflowInstance? WorkflowInstance { get; set; }
}
