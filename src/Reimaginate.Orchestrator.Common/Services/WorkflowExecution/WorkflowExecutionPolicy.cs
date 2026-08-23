using Reimaginate.Orchestrator.Common.Config;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowExecution;

public sealed record WorkflowExecutionPolicy(
    WorkflowCheckpointingMode Checkpoints,
    WorkflowInstancePersistenceMode WorkflowInstances = WorkflowInstancePersistenceMode.Enabled)
{
    public static WorkflowExecutionPolicy Default { get; } = new(WorkflowCheckpointingMode.Enabled);

    public WorkflowExecutionPolicy Apply(WorkflowExecutionPolicyOverride? executionPolicyOverride)
        => executionPolicyOverride is null
            ? this
            : this with
            {
                Checkpoints = executionPolicyOverride.Checkpoints ?? Checkpoints,
                WorkflowInstances = executionPolicyOverride.WorkflowInstances ?? WorkflowInstances
            };
}

public sealed record WorkflowExecutionPolicyOverride(
    WorkflowCheckpointingMode? Checkpoints = null,
    WorkflowInstancePersistenceMode? WorkflowInstances = null);
