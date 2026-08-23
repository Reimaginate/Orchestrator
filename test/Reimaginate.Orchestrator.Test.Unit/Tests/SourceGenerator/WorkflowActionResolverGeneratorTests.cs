using System.Collections.Immutable;
using System.Reflection;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Reimaginate.Orchestrator.SourceGenerator;
using Xunit;

namespace Reimaginate.Orchestrator.Test.Unit.Tests.SourceGenerator;

[Collection(SourceGeneratorTestCollectionDefinition.CollectionName)]
public class WorkflowActionResolverGeneratorTests
{
    [Fact]
    public void Execute_GeneratesDistinctSourceFilesForMultipleResolvers()
    {
        var compilation = CSharpCompilation.Create(
            "Fixture.Host",
            CreateSyntaxTrees(),
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new WorkflowActionResolverGenerator());

        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);
        var runResult = driver.GetRunResult();

        runResult.Diagnostics.Should().BeEmpty();
        runResult.Results.Should().ContainSingle();
        runResult.Results[0].GeneratedSources.Should().HaveCount(2);
        runResult.Results[0].GeneratedSources.Select(sourceResult => sourceResult.HintName)
            .Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Execute_ResolvesStringBasedScanAssemblyMarkers()
    {
        var compilation = CSharpCompilation.Create(
            "Fixture.Host",
            CreateSyntaxTrees(useStringAssemblyMarker: true),
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new WorkflowActionResolverGenerator());

        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);
        var runResult = driver.GetRunResult();

        runResult.Diagnostics.Should().BeEmpty();
        var generatedSource = runResult.Results[0].GeneratedSources.Single().SourceText.ToString();
        generatedSource.Should().Contain("ReverseText");
    }

    private static ImmutableArray<SyntaxTree> CreateSyntaxTrees(bool useStringAssemblyMarker = false)
        => CreateSharedSyntaxTrees()
            .AddRange(CreateWorkflowSyntaxTrees())
            .AddRange(CreateHostSyntaxTrees(useStringAssemblyMarker));

    private static ImmutableArray<SyntaxTree> CreateSharedSyntaxTrees()
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp12);

        return
        [
            CSharpSyntaxTree.ParseText(
                """
                using System;

                namespace Reimaginate.Orchestrator.Abstractions;

                [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
                public sealed class WorkflowActionResolverAttribute : Attribute
                {
                }

                [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
                public sealed class ScanAssemblyAttribute : Attribute
                {
                    public ScanAssemblyAttribute(Type assemblyMarker)
                    {
                        AssemblyMarker = assemblyMarker;
                    }

                    public ScanAssemblyAttribute(string assemblyName)
                    {
                        AssemblyName = assemblyName;
                    }

                    public Type? AssemblyMarker { get; }

                    public string? AssemblyName { get; }
                }

                [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
                public sealed class WorkflowActionAttribute(string name) : Attribute
                {
                    public string Name { get; } = name;
                }

                public interface IWorkflowActionResolver
                {
                    (Type RequestType, Type ResponseType) Resolve(string requestName);
                }
                """,
                parseOptions),
            CSharpSyntaxTree.ParseText(
                """
                namespace Reimaginate.Mediator;

                public interface IRequest<TResponse>
                {
                }
                """,
                parseOptions)
        ];
    }

    private static ImmutableArray<SyntaxTree> CreateWorkflowSyntaxTrees()
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp12);

        return
        [
            CSharpSyntaxTree.ParseText(
                """
                namespace Fixture.Workflows;

                [Reimaginate.Orchestrator.Abstractions.WorkflowAction("ReverseText")]
                public sealed class ReverseTextRequest : Reimaginate.Mediator.IRequest<ReverseTextResponse>
                {
                }

                public sealed class ReverseTextResponse
                {
                }
                """,
                parseOptions)
        ];
    }

    private static ImmutableArray<SyntaxTree> CreateHostSyntaxTrees(bool useStringAssemblyMarker = false)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp12);

        return
        [
            CSharpSyntaxTree.ParseText(
                useStringAssemblyMarker
                    ? """
                namespace Fixture.Host;

                [Reimaginate.Orchestrator.Abstractions.WorkflowActionResolver]
                [Reimaginate.Orchestrator.Abstractions.ScanAssembly("Fixture.Host")]
                public partial class WorkflowActionResolver : Reimaginate.Orchestrator.Abstractions.IWorkflowActionResolver
                {
                }
                """
                    : """
                namespace Fixture.Host;

                [Reimaginate.Orchestrator.Abstractions.WorkflowActionResolver]
                [Reimaginate.Orchestrator.Abstractions.ScanAssembly(typeof(Fixture.Workflows.ReverseTextRequest))]
                public partial class WorkflowActionResolver : Reimaginate.Orchestrator.Abstractions.IWorkflowActionResolver
                {
                }

                [Reimaginate.Orchestrator.Abstractions.WorkflowActionResolver]
                [Reimaginate.Orchestrator.Abstractions.ScanAssembly(typeof(Fixture.Workflows.ReverseTextRequest))]
                public partial class SecondaryWorkflowActionResolver : Reimaginate.Orchestrator.Abstractions.IWorkflowActionResolver
                {
                }
                """,
                parseOptions)
        ];
    }

    private static ImmutableArray<MetadataReference> CreateMetadataReferences()
    {
        var trustedPlatformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        trustedPlatformAssemblies.Should().NotBeNullOrWhiteSpace();

        var references = trustedPlatformAssemblies!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(Binder).GetTypeInfo().Assembly.Location))
            .Distinct(MetadataReferencePathComparer.Instance)
            .ToImmutableArray();

        return references;
    }

    private sealed class MetadataReferencePathComparer : IEqualityComparer<MetadataReference>
    {
        public static MetadataReferencePathComparer Instance { get; } = new();

        public bool Equals(MetadataReference? x, MetadataReference? y)
            => StringComparer.OrdinalIgnoreCase.Equals(GetPath(x), GetPath(y));

        public int GetHashCode(MetadataReference obj)
            => StringComparer.OrdinalIgnoreCase.GetHashCode(GetPath(obj) ?? string.Empty);

        private static string? GetPath(MetadataReference? reference)
            => (reference as PortableExecutableReference)?.FilePath;
    }
}
