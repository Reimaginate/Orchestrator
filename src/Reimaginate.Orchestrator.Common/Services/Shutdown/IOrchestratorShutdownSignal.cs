namespace Reimaginate.Orchestrator.Common.Services.Shutdown;

public interface IOrchestratorShutdownSignal
{
    CancellationToken ShutdownToken { get; }

    bool IsShutdownRequested { get; }
}
