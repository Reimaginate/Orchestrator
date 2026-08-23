using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowInstanceMutation;

public sealed class WorkflowInstanceMutationCommand
{
    private WorkflowInstanceMutationCommand(WorkflowInstance? workflowInstance, bool noChange)
    {
        WorkflowInstance = workflowInstance;
        NoChange = noChange;
    }

    public bool NoChange { get; }
    public WorkflowInstance? WorkflowInstance { get; }

    public static WorkflowInstanceMutationCommand None() => new(null, true);
    public static WorkflowInstanceMutationCommand Write(WorkflowInstance workflowInstance) => new(workflowInstance, false);
}
