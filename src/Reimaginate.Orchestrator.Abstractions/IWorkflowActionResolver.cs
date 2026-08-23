namespace Reimaginate.Orchestrator.Abstractions;

public interface IWorkflowActionResolver
{
    (Type RequestType, Type ResponseType) Resolve(string requestName);
}
