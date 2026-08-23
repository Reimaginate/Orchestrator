using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Reimaginate.Orchestrator.Common.Services.Shutdown;

internal sealed class HostOrchestratorShutdownSignal(IServiceProvider serviceProvider) : IOrchestratorShutdownSignal
{
    private readonly IHostApplicationLifetime? hostApplicationLifetime = serviceProvider.GetService<IHostApplicationLifetime>();

    public CancellationToken ShutdownToken => hostApplicationLifetime?.ApplicationStopping ?? CancellationToken.None;

    public bool IsShutdownRequested => ShutdownToken.IsCancellationRequested;
}
