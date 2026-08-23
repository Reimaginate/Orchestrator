using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.Common;

[WorkflowActionResolver]
[ScanAssembly(typeof(WorkflowActionResolver))]
public partial class WorkflowActionResolver : IWorkflowActionResolver
{
    (Type RequestType, Type ResponseType) IWorkflowActionResolver.Resolve(string requestName)
    {
        var (requestType, responseType) = Resolve(requestName);
        return (requestType, responseType);
    }
}
