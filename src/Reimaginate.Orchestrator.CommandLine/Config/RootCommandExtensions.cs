using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using System.CommandLine;
using Command = System.CommandLine.Command;

namespace Reimaginate.Orchestrator.CommandLine.Config;

public static class RootCommandExtensions
{
    public static RootCommand AddOrchestratorCommandLineCommands(
        this RootCommand rootCommand,
        IServiceProvider serviceProvider,
        params Assembly[] additionalCommandAssemblies)
    {
        ArgumentNullException.ThrowIfNull(rootCommand);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var assemblies = new List<Assembly>
        {
            typeof(RootCommandExtensions).Assembly
        };

        assemblies.AddRange(additionalCommandAssemblies);
        return rootCommand.AddCommandsFromAssemblies(serviceProvider, [.. assemblies.Distinct()]);
    }

    public static RootCommand AddCommandsFromAssemblies(
        this RootCommand rootCommand,
        IServiceProvider serviceProvider,
        params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(rootCommand);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(assemblies);

        foreach (var commandType in CommandTypeDiscovery.GetTopLevelCommandTypes(assemblies))
        {
            var command = (Command)serviceProvider.GetRequiredService(commandType);
            if (rootCommand.Subcommands.Any(existing => existing.Name == command.Name))
            {
                continue;
            }

            rootCommand.Add(command);
        }

        return rootCommand;
    }
}
