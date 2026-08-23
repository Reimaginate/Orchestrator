namespace Reimaginate.Orchestrator.Common.Config;

public sealed class OrchestratorWorkflowInstanceMutationOptions
{
    public int MaxMutationAttempts { get; set; } = 5;
    public int MaxLockAttempts { get; set; } = 600;
    public int LockRetryDelayMilliseconds { get; set; } = 50;
}
