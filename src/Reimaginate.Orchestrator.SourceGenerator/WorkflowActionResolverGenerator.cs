using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reimaginate.Orchestrator.SourceGenerator;

[Generator(LanguageNames.CSharp)]
public sealed class WorkflowActionResolverGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var resolverDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, _) => TransformResolverDeclaration(ctx))
            .Where(static info => info is not null)
            .Select(static (info, _) => info!)
            .Collect();

        context.RegisterSourceOutput(resolverDeclarations, static (spc, resolvers) =>
        {
            foreach (var resolver in resolvers)
            {
                GenerateResolver(spc, resolver);
            }
        });
    }

    private static ResolverInfo? TransformResolverDeclaration(GeneratorSyntaxContext context)
    {
        if (context.Node is not ClassDeclarationSyntax classDeclaration)
        {
            return null;
        }

        if (context.SemanticModel.GetDeclaredSymbol(classDeclaration) is not INamedTypeSymbol classSymbol)
        {
            return null;
        }

        var resolverAttribute = classSymbol.GetAttributes()
            .FirstOrDefault(static attr => attr.AttributeClass?.ToDisplayString() == WorkflowDiscoveryNames.WorkflowActionResolverAttribute);

        if (resolverAttribute is null)
        {
            return null;
        }

        var scannedAssemblies = GetScannedAssemblies(classSymbol, context.SemanticModel.Compilation);

        var workflowActions = new List<WorkflowActionInfo>();
        foreach (var assembly in scannedAssemblies)
        {
            foreach (var type in GetTypes(assembly.GlobalNamespace))
            {
                var workflowActionAttribute = type.GetAttributes()
                    .FirstOrDefault(static attr => attr.AttributeClass?.ToDisplayString() == WorkflowDiscoveryNames.WorkflowActionAttribute);

                if (workflowActionAttribute is null || workflowActionAttribute.ConstructorArguments.Length == 0)
                {
                    continue;
                }

                if (workflowActionAttribute.ConstructorArguments[0].Value is not string requestName
                    || string.IsNullOrWhiteSpace(requestName))
                {
                    continue;
                }

                workflowActions.Add(new WorkflowActionInfo(requestName, type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
            }
        }

        var distinctActions = workflowActions
            .GroupBy(static action => action.Name, StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static action => action.Name, StringComparer.Ordinal)
            .ToImmutableArray();

        return new ResolverInfo(
            classSymbol.ContainingNamespace.ToDisplayString(),
            classSymbol.Name,
            distinctActions);
    }

    private static List<IAssemblySymbol> GetScannedAssemblies(INamedTypeSymbol classSymbol, Compilation compilation)
    {
        var scanAssemblyAttributes = classSymbol.GetAttributes()
            .Where(static attr => attr.AttributeClass?.ToDisplayString() == WorkflowDiscoveryNames.ScanAssemblyAttribute);

        var assemblies = new List<IAssemblySymbol>();
        var identityNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var scanAssemblyAttribute in scanAssemblyAttributes)
        {
            if (scanAssemblyAttribute.ConstructorArguments.Length == 0)
            {
                continue;
            }

            if (scanAssemblyAttribute.ConstructorArguments[0].Value is not INamedTypeSymbol markerSymbol)
            {
                if (scanAssemblyAttribute.ConstructorArguments[0].Value is string assemblyName
                    && AssemblySymbolResolver.TryResolveAssemblyByName(compilation, assemblyName, additionalAssemblies: null, out var namedAssembly)
                    && identityNames.Add(namedAssembly.Identity.Name))
                {
                    assemblies.Add(namedAssembly);
                }

                continue;
            }

            var containingAssembly = markerSymbol.ContainingAssembly;
            if (containingAssembly is null)
            {
                continue;
            }

            if (identityNames.Add(containingAssembly.Identity.Name))
            {
                assemblies.Add(containingAssembly);
            }
        }

        return assemblies;
    }

    private static IEnumerable<INamedTypeSymbol> GetTypes(INamespaceSymbol ns)
    {
        foreach (var nestedNamespace in ns.GetNamespaceMembers())
        {
            foreach (var type in GetTypes(nestedNamespace))
            {
                yield return type;
            }
        }

        foreach (var type in ns.GetTypeMembers())
        {
            yield return type;
        }
    }

    private static void GenerateResolver(SourceProductionContext context, ResolverInfo resolver)
    {
        var entriesBuilder = new StringBuilder();
        foreach (var action in resolver.Actions)
        {
            entriesBuilder.AppendLine($"        {{\"{EscapeString(action.Name)}\", typeof({action.TypeName})}},");
        }

        var source = $$"""
using System;
using System.Collections.Generic;
using Reimaginate.Mediator;

namespace {{resolver.Namespace}};

public partial class {{resolver.ClassName}}
{
    private static readonly Dictionary<string, Type> RequestTypesByName = new()
    {
{{entriesBuilder.ToString()}}    };

    public (Type RequestType, Type ResponseType) Resolve(string requestName)
    {
        if (!RequestTypesByName.TryGetValue(requestName, out var requestType))
        {
            throw new InvalidOperationException($"Request type '{requestName}' is not allowed.");
        }

        var requestInterface = requestType.GetInterface(typeof(IRequest<>).Name);
        if (requestInterface is null || requestInterface.GenericTypeArguments.Length != 1)
        {
            throw new InvalidOperationException($"Request type '{requestType.FullName}' must implement IRequest<TResponse>.");
        }

        var responseType = requestInterface.GenericTypeArguments[0];

        return (requestType, responseType);
    }
}
""";

        var sourceHintName = CreateHintName(resolver);
        context.AddSource(sourceHintName, source);
    }

    private static string EscapeString(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string CreateHintName(ResolverInfo resolver)
    {
        var displayName = string.IsNullOrWhiteSpace(resolver.Namespace)
            ? resolver.ClassName
            : resolver.Namespace + "." + resolver.ClassName;

        return displayName
            .Replace('<', '_')
            .Replace('>', '_')
            .Replace(':', '_')
            .Replace('/', '_')
            .Replace('\\', '_')
            + ".WorkflowActionResolver.g.cs";
    }

    private sealed class ResolverInfo
    {
        public ResolverInfo(string @namespace, string className, ImmutableArray<WorkflowActionInfo> actions)
        {
            Namespace = @namespace;
            ClassName = className;
            Actions = actions;
        }

        public string Namespace { get; }

        public string ClassName { get; }

        public ImmutableArray<WorkflowActionInfo> Actions { get; }
    }

    private sealed class WorkflowActionInfo
    {
        public WorkflowActionInfo(string name, string typeName)
        {
            Name = name;
            TypeName = typeName;
        }

        public string Name { get; }

        public string TypeName { get; }
    }
}
