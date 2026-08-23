namespace Reimaginate.Orchestrator.Common.Config;

public sealed class OrchestratorEnvironmentOptions
{
    public List<string> Variables { get; set; } = [];
    public List<string> Prefixes { get; set; } = [];
    public List<OrchestratorWorkflowEnvironmentOptions> Workflows { get; set; } = [];
}

public sealed class OrchestratorWorkflowEnvironmentOptions
{
    public string? WorkflowType { get; set; }
    public List<string> Variables { get; set; } = [];
    public List<string> Prefixes { get; set; } = [];
    public OrchestratorWorkflowEnvironmentExecutionOptions Execution { get; set; } = new();
}

public sealed class OrchestratorWorkflowEnvironmentExecutionOptions
{
    public WorkflowCheckpointingMode Checkpoints { get; set; } = WorkflowCheckpointingMode.Enabled;
    public WorkflowInstancePersistenceMode WorkflowInstances { get; set; } = WorkflowInstancePersistenceMode.Enabled;
}

public enum WorkflowCheckpointingMode
{
    Enabled = 0,
    Disabled = 1,
    Failure = 2
}

public enum WorkflowInstancePersistenceMode
{
    Enabled = 0,
    Disabled = 1,
    Failure = 2
}
