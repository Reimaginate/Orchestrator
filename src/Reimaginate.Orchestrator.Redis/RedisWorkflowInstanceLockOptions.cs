namespace Reimaginate.Orchestrator.Redis;

public sealed class RedisWorkflowInstanceLockOptions
{
    public string KeyPrefix { get; set; } = "orchestrator:workflow-instance-lock:";
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan? RenewalInterval { get; set; }
    public int RenewalFailureThreshold { get; set; } = 3;
    public RedisWorkflowInstanceLockReleaseFailureBehavior ReleaseFailureBehavior { get; set; } = RedisWorkflowInstanceLockReleaseFailureBehavior.Suppress;
}
