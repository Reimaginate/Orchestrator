using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Reimaginate.Orchestrator.SourceGenerator;

public static class WorkflowActionSymbolDiscovery
{
    private static readonly SymbolDisplayFormat TypeDisplayFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces);

    public static WorkflowActionDiscoveryResult Discover(SourceProjectModel hostProject)
    {
        ArgumentNullException.ThrowIfNull(hostProject);

        var resolverTypes = CollectResolverSpecifications(hostProject)
            .OrderBy(resolver => resolver.DisplayName, StringComparer.Ordinal)
            .ToArray();

        if (resolverTypes.Length == 0)
        {
            return WorkflowActionDiscoveryResult.NoResolvers();
        }

        var actions = new Dictionary<string, WorkflowActionDescriptor>(StringComparer.Ordinal);
        var scannedAssemblies = new HashSet<string>(StringComparer.Ordinal);

        foreach (var resolverType in resolverTypes)
        {
            if (!string.IsNullOrWhiteSpace(resolverType.UnresolvedReason))
            {
                return WorkflowActionDiscoveryResult.CreateUnresolved(
                    resolverTypes.Select(resolver => resolver.DisplayName),
                    resolverType.UnresolvedReason!);
            }

            foreach (var markerReference in resolverType.ScanAssemblyMarkers)
            {
                if (TryResolveAssembly(hostProject, markerReference, out var containingAssembly, out var unresolvedReason) is false)
                {
                    return WorkflowActionDiscoveryResult.CreateUnresolved(
                        resolverTypes.Select(resolver => resolver.DisplayName),
                        unresolvedReason ?? $"Resolver '{resolverType.DisplayName}' contains an unresolved ScanAssembly marker.");
                }

                if (!scannedAssemblies.Add(containingAssembly.Identity.Name))
                {
                    continue;
                }

                foreach (var action in GetWorkflowActions(hostProject, containingAssembly))
                {
                    actions.TryAdd(action.Name, action);
                }
            }
        }

        return WorkflowActionDiscoveryResult.CreateSuccess(resolverTypes.Select(resolver => resolver.DisplayName), actions.Values);
    }

    private static IEnumerable<WorkflowActionDescriptor> GetWorkflowActions(SourceProjectModel hostProject, IAssemblySymbol assemblySymbol)
    {
        var sourceProject = FindProjectByAssemblyName(hostProject, assemblySymbol.Identity.Name);
        if (sourceProject is not null)
        {
            foreach (var action in GetWorkflowActions(sourceProject))
            {
                yield return action;
            }

            yield break;
        }

        foreach (var action in GetWorkflowActions(assemblySymbol))
        {
            yield return action;
        }
    }

    private static IEnumerable<WorkflowActionDescriptor> GetWorkflowActions(IAssemblySymbol assemblySymbol)
    {
        foreach (var type in GetAllTypes(assemblySymbol.GlobalNamespace))
        {
            foreach (var attribute in type.GetAttributes().Where(IsWorkflowActionAttribute))
            {
                if (attribute.ConstructorArguments.Length == 0)
                {
                    continue;
                }

                if (attribute.ConstructorArguments[0].Value is string actionName
                    && !string.IsNullOrWhiteSpace(actionName))
                {
                    var responseType = ResolveResponseType(type);
                    yield return new WorkflowActionDescriptor
                    {
                        Name = actionName,
                        RequestType = type.ToDisplayString(TypeDisplayFormat),
                        ResponseType = responseType?.ToDisplayString(TypeDisplayFormat),
                        RequestProperties = GetPublicInstanceProperties(type, requireWritable: true),
                        ResponseProperties = responseType is null
                            ? []
                            : GetPublicInstanceProperties(responseType, requireWritable: false)
                    };
                }
            }
        }
    }

    private static IEnumerable<WorkflowActionDescriptor> GetWorkflowActions(SourceProjectModel projectModel)
    {
        foreach (var sourceType in projectModel.SourceTypes.Where(type => type.WorkflowActions.Count > 0))
        {
            var requestType = ResolveType(projectModel.Compilation, sourceType.FullName)
                ?? ResolveType(projectModel.Compilation, sourceType.Name);
            var responseType = requestType is null ? null : ResolveResponseType(requestType);

            foreach (var actionName in sourceType.WorkflowActions.Where(actionName => !string.IsNullOrWhiteSpace(actionName)))
            {
                yield return new WorkflowActionDescriptor
                {
                    Name = actionName,
                    RequestType = requestType?.ToDisplayString(TypeDisplayFormat) ?? sourceType.FullName,
                    ResponseType = responseType?.ToDisplayString(TypeDisplayFormat),
                    RequestProperties = requestType is null
                        ? []
                        : GetPublicInstanceProperties(requestType, requireWritable: true),
                    ResponseProperties = responseType is null
                        ? []
                        : GetPublicInstanceProperties(responseType, requireWritable: false)
                };
            }
        }
    }

    private static IReadOnlyList<string> GetPublicInstanceProperties(INamedTypeSymbol typeSymbol, bool requireWritable)
    {
        var typeHierarchy = new Stack<INamedTypeSymbol>();
        for (var current = typeSymbol; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            typeHierarchy.Push(current);
        }

        var properties = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (typeHierarchy.Count > 0)
        {
            foreach (var property in typeHierarchy.Pop().GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsStatic
                    || property.IsIndexer
                    || property.DeclaredAccessibility != Accessibility.Public
                    || property.GetMethod?.DeclaredAccessibility != Accessibility.Public)
                {
                    continue;
                }

                if (requireWritable && property.SetMethod?.DeclaredAccessibility != Accessibility.Public)
                {
                    continue;
                }

                if (names.Add(property.Name))
                {
                    properties.Add(property.Name);
                }
            }
        }

        return properties;
    }

    private static INamedTypeSymbol? ResolveResponseType(INamedTypeSymbol requestType)
        => requestType.AllInterfaces
            .FirstOrDefault(static candidate => candidate.Name == "IRequest" && candidate.TypeArguments.Length == 1)?
            .TypeArguments[0] as INamedTypeSymbol;

    private static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol namespaceSymbol)
    {
        foreach (var nestedNamespace in namespaceSymbol.GetNamespaceMembers())
        {
            foreach (var type in GetAllTypes(nestedNamespace))
            {
                yield return type;
            }
        }

        foreach (var type in namespaceSymbol.GetTypeMembers())
        {
            foreach (var nestedType in GetAllTypes(type))
            {
                yield return nestedType;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> GetAllTypes(INamedTypeSymbol typeSymbol)
    {
        yield return typeSymbol;

        foreach (var nestedType in typeSymbol.GetTypeMembers())
        {
            foreach (var nested in GetAllTypes(nestedType))
            {
                yield return nested;
            }
        }
    }

    private static IReadOnlyList<ResolverSpecification> CollectResolverSpecifications(SourceProjectModel hostProject)
    {
        var resolvers = new Dictionary<string, ResolverSpecification>(StringComparer.Ordinal);

        foreach (var sourceResolver in hostProject.SourceTypes
            .Where(type => type.IsWorkflowActionResolver)
            .OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            resolvers[sourceResolver.FullName] = new ResolverSpecification
            {
                DisplayName = sourceResolver.FullName,
                ScanAssemblyMarkers = sourceResolver.ScanAssemblyMarkers
                    .Where(marker => !string.IsNullOrWhiteSpace(marker))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            };
        }

        foreach (var resolverType in GetAllTypes(hostProject.Compilation.Assembly.GlobalNamespace)
            .Where(type => type.Locations.Any(location => location.IsInSource))
            .Where(IsWorkflowActionResolver)
            .OrderBy(type => type.ToDisplayString(TypeDisplayFormat), StringComparer.Ordinal))
        {
            var displayName = resolverType.ToDisplayString(TypeDisplayFormat);
            var symbolResolver = CreateResolverSpecification(resolverType);
            if (!resolvers.TryGetValue(displayName, out var existingResolver))
            {
                resolvers[displayName] = symbolResolver;
                continue;
            }

            resolvers[displayName] = MergeResolverSpecifications(existingResolver, symbolResolver);
        }

        return resolvers.Values.ToArray();
    }

    private static SourceProjectModel? FindProjectByAssemblyName(SourceProjectModel rootProject, string assemblyName)
    {
        foreach (var project in EnumerateProjects(rootProject))
        {
            if (string.Equals(project.ProjectInfo.AssemblyName, assemblyName, StringComparison.OrdinalIgnoreCase))
            {
                return project;
            }
        }

        return null;
    }

    private static IEnumerable<SourceProjectModel> EnumerateProjects(SourceProjectModel rootProject)
    {
        yield return rootProject;

        foreach (var referencedProject in rootProject.ReferencedProjects)
        {
            foreach (var project in EnumerateProjects(referencedProject))
            {
                yield return project;
            }
        }
    }

    private static ResolverSpecification CreateResolverSpecification(INamedTypeSymbol resolverType)
    {
        var markers = new List<string>();

        foreach (var scanAssemblyAttribute in resolverType.GetAttributes().Where(IsScanAssemblyAttribute))
        {
            if (scanAssemblyAttribute.ConstructorArguments.Length == 0)
            {
                return new ResolverSpecification
                {
                    DisplayName = resolverType.ToDisplayString(TypeDisplayFormat),
                    UnresolvedReason = $"Resolver '{resolverType.ToDisplayString(TypeDisplayFormat)}' contains an unresolved ScanAssembly marker."
                };
            }

            if (scanAssemblyAttribute.ConstructorArguments[0].Value is INamedTypeSymbol markerType)
            {
                if (markerType.TypeKind == TypeKind.Error)
                {
                    return new ResolverSpecification
                    {
                        DisplayName = resolverType.ToDisplayString(TypeDisplayFormat),
                        UnresolvedReason = $"Resolver '{resolverType.ToDisplayString(TypeDisplayFormat)}' contains an unresolved ScanAssembly marker."
                    };
                }

                markers.Add(markerType.ToDisplayString(TypeDisplayFormat));
                continue;
            }

            if (scanAssemblyAttribute.ConstructorArguments[0].Value is string assemblyName
                && !string.IsNullOrWhiteSpace(assemblyName))
            {
                markers.Add(SourceTypeDeclaration.AssemblyReferencePrefix + assemblyName);
                continue;
            }

            return new ResolverSpecification
            {
                DisplayName = resolverType.ToDisplayString(TypeDisplayFormat),
                UnresolvedReason = $"Resolver '{resolverType.ToDisplayString(TypeDisplayFormat)}' contains an unresolved ScanAssembly marker."
            };
        }

        return new ResolverSpecification
        {
            DisplayName = resolverType.ToDisplayString(TypeDisplayFormat),
            ScanAssemblyMarkers = markers
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
    }

    private static ResolverSpecification MergeResolverSpecifications(ResolverSpecification existingResolver, ResolverSpecification symbolResolver)
    {
        if (symbolResolver.ScanAssemblyMarkers.Count > 0)
        {
            return symbolResolver;
        }

        if (existingResolver.ScanAssemblyMarkers.Count > 0)
        {
            return new ResolverSpecification
            {
                DisplayName = existingResolver.DisplayName,
                ScanAssemblyMarkers = existingResolver.ScanAssemblyMarkers
            };
        }

        return string.IsNullOrWhiteSpace(symbolResolver.UnresolvedReason)
            ? symbolResolver
            : existingResolver;
    }

    private static INamedTypeSymbol? ResolveType(Compilation compilation, string typeReference)
    {
        var normalizedReference = TrimGenericArity(TrimGlobalQualifier(typeReference));
        if (string.IsNullOrWhiteSpace(normalizedReference))
        {
            return null;
        }

        var exactMatches = GetAllTypes(compilation.Assembly.GlobalNamespace)
            .Concat(compilation.SourceModule.ReferencedAssemblySymbols.SelectMany(assembly => GetAllTypes(assembly.GlobalNamespace)))
            .Where(type => MatchesTypeReference(type, normalizedReference))
            .ToArray();

        if (exactMatches.Length == 1)
        {
            return exactMatches[0];
        }

        var fullNameMatches = exactMatches
            .Where(type => string.Equals(type.ToDisplayString(TypeDisplayFormat), normalizedReference, StringComparison.Ordinal))
            .ToArray();
        if (fullNameMatches.Length == 1)
        {
            return fullNameMatches[0];
        }

        var nameMatches = exactMatches
            .Where(type => string.Equals(type.Name, normalizedReference, StringComparison.Ordinal))
            .ToArray();
        return nameMatches.Length == 1 ? nameMatches[0] : null;
    }

    private static bool TryResolveAssembly(
        SourceProjectModel hostProject,
        string markerReference,
        out IAssemblySymbol containingAssembly,
        out string? unresolvedReason)
    {
        containingAssembly = null!;
        unresolvedReason = null;

        if (markerReference.StartsWith(SourceTypeDeclaration.AssemblyReferencePrefix, StringComparison.Ordinal))
        {
            var assemblyName = markerReference[SourceTypeDeclaration.AssemblyReferencePrefix.Length..];
            if (string.IsNullOrWhiteSpace(assemblyName))
            {
                unresolvedReason = "ScanAssembly assembly name cannot be empty.";
                return false;
            }

            var additionalAssemblies = EnumerateProjects(hostProject)
                .Select(project => project.Compilation.Assembly)
                .Where(projectAssembly =>
                    !string.Equals(projectAssembly.Identity.Name, hostProject.Compilation.Assembly.Identity.Name, StringComparison.OrdinalIgnoreCase));
            var resolved = AssemblySymbolResolver.TryResolveAssemblyByName(
                hostProject.Compilation,
                assemblyName,
                additionalAssemblies,
                out var resolvedAssembly);

            if (!resolved || resolvedAssembly is null)
            {
                unresolvedReason = $"Resolver '{hostProject.ProjectInfo.AssemblyName}.WorkflowActionResolver' contains an unresolved ScanAssembly marker.";
                return false;
            }

            containingAssembly = resolvedAssembly;
            return true;
        }

        var markerType = ResolveType(hostProject.Compilation, markerReference);
        if (markerType is null || markerType.TypeKind == TypeKind.Error)
        {
            unresolvedReason = $"Resolver '{hostProject.ProjectInfo.AssemblyName}.WorkflowActionResolver' contains an unresolved ScanAssembly marker.";
            return false;
        }

        containingAssembly = markerType.ContainingAssembly!;
        if (containingAssembly is null)
        {
            unresolvedReason = $"Resolver '{hostProject.ProjectInfo.AssemblyName}.WorkflowActionResolver' contains a ScanAssembly marker without a containing assembly.";
            return false;
        }

        return true;
    }

    private static bool MatchesTypeReference(INamedTypeSymbol typeSymbol, string typeReference)
    {
        var fullName = typeSymbol.ToDisplayString(TypeDisplayFormat);
        return string.Equals(fullName, typeReference, StringComparison.Ordinal)
            || string.Equals(typeSymbol.Name, typeReference, StringComparison.Ordinal);
    }

    private static string TrimGlobalQualifier(string typeReference)
        => typeReference.StartsWith("global::", StringComparison.Ordinal) ? typeReference["global::".Length..] : typeReference;

    private static string TrimGenericArity(string typeReference)
    {
        var genericIndex = typeReference.IndexOf('<');
        return genericIndex >= 0 ? typeReference[..genericIndex] : typeReference;
    }

    private static bool IsWorkflowActionResolver(INamedTypeSymbol typeSymbol)
        => typeSymbol.GetAttributes().Any(attribute =>
            AttributeNameMatches(attribute.AttributeClass, WorkflowDiscoveryNames.WorkflowActionResolverAttribute, "WorkflowActionResolver"));

    private static bool IsWorkflowActionAttribute(AttributeData attribute)
        => AttributeNameMatches(attribute.AttributeClass, WorkflowDiscoveryNames.WorkflowActionAttribute, "WorkflowAction");

    private static bool IsScanAssemblyAttribute(AttributeData attribute)
        => AttributeNameMatches(attribute.AttributeClass, WorkflowDiscoveryNames.ScanAssemblyAttribute, "ScanAssembly");

    private static bool AttributeNameMatches(INamedTypeSymbol? attributeClass, string expectedFullName, string expectedShortName)
    {
        var displayName = attributeClass?.ToDisplayString();
        var name = attributeClass?.Name;

        return string.Equals(displayName, expectedFullName, StringComparison.Ordinal)
            || string.Equals(name, expectedShortName, StringComparison.Ordinal)
            || string.Equals(name, expectedShortName + "Attribute", StringComparison.Ordinal)
            || (!string.IsNullOrWhiteSpace(displayName) && displayName.EndsWith("." + expectedShortName, StringComparison.Ordinal))
            || (!string.IsNullOrWhiteSpace(displayName) && displayName.EndsWith("." + expectedShortName + "Attribute", StringComparison.Ordinal));
    }
}

public sealed class WorkflowActionDiscoveryResult
{
    public bool HasResolvers { get; init; }

    public bool Success { get; init; }

    public string? Reason { get; init; }

    public IReadOnlyList<string> ResolverTypes { get; init; } = [];

    public IReadOnlyList<WorkflowActionDescriptor> Actions { get; init; } = [];

    public static WorkflowActionDiscoveryResult NoResolvers()
        => new()
        {
            HasResolvers = false,
            Success = false
        };

    public static WorkflowActionDiscoveryResult CreateSuccess(IEnumerable<string> resolverTypes, IEnumerable<WorkflowActionDescriptor> actions)
        => new()
        {
            HasResolvers = true,
            Success = true,
            ResolverTypes = resolverTypes
                .Where(resolverType => !string.IsNullOrWhiteSpace(resolverType))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray(),
            Actions = actions
                .Where(action => !string.IsNullOrWhiteSpace(action.Name))
                .GroupBy(action => action.Name, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(action => action.Name, StringComparer.Ordinal)
                .ToArray()
        };

    public static WorkflowActionDiscoveryResult CreateUnresolved(IEnumerable<string> resolverTypes, string reason)
        => new()
        {
            HasResolvers = true,
            Success = false,
            Reason = reason,
            ResolverTypes = resolverTypes
                .Where(resolverType => !string.IsNullOrWhiteSpace(resolverType))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray()
        };
}

public sealed class WorkflowActionDescriptor
{
    public string Name { get; init; } = string.Empty;

    public string RequestType { get; init; } = string.Empty;

    public string? ResponseType { get; init; }

    public IReadOnlyList<string> RequestProperties { get; init; } = [];

    public IReadOnlyList<string> ResponseProperties { get; init; } = [];
}

internal sealed class ResolverSpecification
{
    public string DisplayName { get; init; } = string.Empty;

    public string? UnresolvedReason { get; init; }

    public IReadOnlyList<string> ScanAssemblyMarkers { get; init; } = [];
}
