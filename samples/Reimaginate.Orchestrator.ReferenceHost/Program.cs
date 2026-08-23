using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Reimaginate.Mediator;
using Reimaginate.Orchestrator.CommandLine.Config;
using Reimaginate.Orchestrator.Common.Config;
using Reimaginate.Orchestrator.ReferenceHost;
using System.CommandLine;

var builder = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration(configuration =>
    {
        configuration.Sources.Clear();
        configuration.AddJsonFile("appsettings.json", optional: false);
        configuration.AddEnvironmentVariables();
    })
    .ConfigureServices((context, services) =>
    {
        services.AddSingleton<IMediator, Mediator>();
        Mediator.RegisterHandlers(services);
        services.AddOrchestratorServices(context.Configuration, typeof(WorkflowActionResolver));
        services.AddOrchestratorCommandLine();
    });

using var host = builder.Build();
await host.StartAsync();

try
{
    var rootCommand = new RootCommand();
    rootCommand.AddOrchestratorCommandLineCommands(host.Services);
    return await rootCommand.Parse(args).InvokeAsync();
}
finally
{
    await host.StopAsync(CancellationToken.None);
}
