using System.Reflection;
using Reimaginate.CLI.Base.Abstractions;

namespace Reimaginate.Orchestrator.CommandLine.Config;

internal static class CommandTypeDiscovery
{
    public static IReadOnlyList<Type> GetTopLevelCommandTypes(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        return assemblies
            .Distinct()
            .SelectMany(static assembly => assembly.GetExportedTypes())
            .Where(static type => typeof(TopLevelCommand).IsAssignableFrom(type) && type != typeof(TopLevelCommand))
            .ToList();
    }

    public static IReadOnlyList<(Type ServiceType, Type ImplementationType)> GetSubCommandTypes(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        return assemblies
            .Distinct()
            .SelectMany(static assembly => assembly.GetExportedTypes())
            .Select(static type => (ImplementationType: type, BaseType: type.BaseType))
            .Where(static entry => (entry.BaseType?.IsGenericType ?? false)
                                   && entry.BaseType?.GetGenericTypeDefinition() == typeof(SubCommand<>))
            .Select(static entry => (entry.BaseType!, entry.ImplementationType))
            .ToList();
    }
}
