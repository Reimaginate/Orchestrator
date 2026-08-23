using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Reimaginate.Orchestrator.SourceGenerator;

internal static class AssemblySymbolResolver
{
    public static bool TryResolveAssemblyByName(
        Compilation compilation,
        string assemblyName,
        IEnumerable<IAssemblySymbol>? additionalAssemblies,
        out IAssemblySymbol assembly)
    {
        assembly = null!;

        if (string.IsNullOrWhiteSpace(assemblyName))
        {
            return false;
        }

        var candidateAssemblies = EnumerateAssemblies(compilation, additionalAssemblies);
        assembly = candidateAssemblies.FirstOrDefault(candidate =>
            string.Equals(candidate.Identity.Name, assemblyName, StringComparison.OrdinalIgnoreCase))!;

        return assembly is not null;
    }

    private static IEnumerable<IAssemblySymbol> EnumerateAssemblies(
        Compilation compilation,
        IEnumerable<IAssemblySymbol>? additionalAssemblies)
    {
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (identities.Add(compilation.Assembly.Identity.Name))
        {
            yield return compilation.Assembly;
        }

        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            if (identities.Add(assembly.Identity.Name))
            {
                yield return assembly;
            }
        }

        if (additionalAssemblies is null)
        {
            yield break;
        }

        foreach (var assembly in additionalAssemblies)
        {
            if (identities.Add(assembly.Identity.Name))
            {
                yield return assembly;
            }
        }
    }
}
