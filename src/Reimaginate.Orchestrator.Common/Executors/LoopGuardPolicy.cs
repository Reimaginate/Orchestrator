namespace Reimaginate.Orchestrator.Common.Executors;

internal sealed record LoopGuardPolicy(
    int MaxIterations,
    int MaxRepeatedPayloads,
    TimeSpan? Timeout,
    LoopGuardCancelMode CancelMode)
{
    // Baseline safeguards tuned for short-running orchestration loops.
    public static readonly LoopGuardPolicy Default = new(
        MaxIterations: 64,
        MaxRepeatedPayloads: 8,
        Timeout: null,
        CancelMode: LoopGuardCancelMode.Fail);
}

internal enum LoopGuardCancelMode
{
    Fail,
    Exit
}
