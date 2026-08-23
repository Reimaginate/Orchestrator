namespace Reimaginate.Orchestrator.Common.Executors;

internal enum WorkflowLoopGuardFailureReason
{
    MaxIterationsExceeded,
    CycleDetected,
    TimeoutExceeded,
    Cancelled
}

internal sealed class WorkflowLoopGuardException(
    string loopId,
    WorkflowLoopGuardFailureReason reason,
    string message,
    int iterations,
    int repeatedPayloadCount,
    int? limit,
    TimeSpan? timeout,
    LoopGuardCancelMode cancelMode) : InvalidOperationException(message)
{
    #region Captured guard details

    public string LoopId { get; } = loopId;

    public WorkflowLoopGuardFailureReason Reason { get; } = reason;

    public int Iterations { get; } = iterations;

    public int RepeatedPayloadCount { get; } = repeatedPayloadCount;

    public int? Limit { get; } = limit;

    public TimeSpan? Timeout { get; } = timeout;

    public LoopGuardCancelMode CancelMode { get; } = cancelMode;

    #endregion

    #region Exception formatting

    public override string Message =>
        $"[{Reason}] loop '{LoopId}' failed: {base.Message} (iterations={Iterations}, repeatedPayloads={RepeatedPayloadCount}, limit={Limit?.ToString() ?? "n/a"}, timeoutMs={(Timeout?.TotalMilliseconds.ToString() ?? "n/a")}, onCancel={CancelMode})";

    #endregion
}
