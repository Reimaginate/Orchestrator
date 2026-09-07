using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Reimaginate.Orchestrator.CommandLine.Config;

public static class ConfigureCommandLine
{
    public static IServiceCollection AddOrchestratorCommandLine(
        this IServiceCollection services,
        params Assembly[] additionalCommandAssemblies)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<OrchestratorCommandLineOptions>();
        return services.AddCommandTypesFromAssemblies(GetCommandAssemblies(additionalCommandAssemblies));
    }

    public static IServiceCollection AddOrchestratorCommandLine(
        this IServiceCollection services,
        IConfiguration configuration,
        params Assembly[] additionalCommandAssemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<OrchestratorCommandLineOptions>(configuration.GetSection(OrchestratorCommandLineOptions.SectionName));
        return services.AddOrchestratorCommandLine(additionalCommandAssemblies);
    }

    public static IServiceCollection AddCommandTypesFromAssemblies(
        this IServiceCollection services,
        params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);

        foreach (var topLevelCommandType in CommandTypeDiscovery.GetTopLevelCommandTypes(assemblies))
        {
            services.TryAddSingleton(topLevelCommandType, topLevelCommandType);
        }

        foreach (var (serviceType, implementationType) in CommandTypeDiscovery.GetSubCommandTypes(assemblies))
        {
            services.TryAddSingleton(implementationType, implementationType);

            if (!services.Any(descriptor => descriptor.ServiceType == serviceType
                                            && descriptor.ImplementationType == implementationType))
            {
                services.AddSingleton(serviceType, implementationType);
            }
        }

        return services;
    }

    private static Assembly[] GetCommandAssemblies(IEnumerable<Assembly>? additionalAssemblies = null)
    {
        var assemblies = new List<Assembly>
        {
            typeof(ConfigureCommandLine).Assembly
        };

        if (additionalAssemblies != null)
        {
            assemblies.AddRange(additionalAssemblies);
        }

        return [.. assemblies.Distinct()];
    }
}
