using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reimaginate.Orchestrator.SourceGenerator;

public sealed class SourceProjectModel
{
    public MsBuildProjectInfo ProjectInfo { get; init; } = new();

    public CSharpCompilation Compilation { get; init; } = null!;

    public IReadOnlyList<SourceProjectModel> ReferencedProjects { get; init; } = [];

    public IReadOnlyList<SourceTypeDeclaration> SourceTypes { get; init; } = [];
}

public sealed class SourceTypeDeclaration
{
    internal const string AssemblyReferencePrefix = "assembly:";

    public string Name { get; init; } = string.Empty;

    public string FullName { get; init; } = string.Empty;

    public bool IsWorkflowActionResolver { get; init; }

    public IReadOnlyList<string> ScanAssemblyMarkers { get; init; } = [];

    public IReadOnlyList<string> WorkflowActions { get; init; } = [];
}

public sealed class SourceProjectModelLoader
{
    private readonly Dictionary<string, Task<SourceProjectModel>> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly MsBuildProjectLoadOptions? loadOptions;

    public SourceProjectModelLoader(MsBuildProjectLoadOptions? loadOptions = null)
    {
        this.loadOptions = loadOptions;
    }

    public Task<SourceProjectModel> LoadAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var normalizedProjectPath = Path.GetFullPath(projectPath);
        if (!cache.TryGetValue(normalizedProjectPath, out var task))
        {
            task = LoadCoreAsync(normalizedProjectPath, cancellationToken);
            cache[normalizedProjectPath] = task;
        }

        return task;
    }

    private async Task<SourceProjectModel> LoadCoreAsync(string projectPath, CancellationToken cancellationToken)
    {
        var effectiveLoadOptions = string.Equals(projectPath, loadOptions?.ProjectPath, StringComparison.OrdinalIgnoreCase)
            ? loadOptions
            : loadOptions?.ForReferencedProject(projectPath);
        var projectInfo = await MsBuildProjectInfoLoader.LoadAsync(projectPath, effectiveLoadOptions, cancellationToken);
        var projectReferences = await LoadProjectReferencesAsync(projectInfo.ProjectReferences, cancellationToken);

        var parseOptions = new CSharpParseOptions(
            MsBuildProjectInfoLoader.ParseLanguageVersion(projectInfo.LanguageVersion),
            DocumentationMode.Parse,
            SourceCodeKind.Regular);
        var syntaxTrees = await LoadSyntaxTreesAsync(projectInfo.CompileFiles, parseOptions, cancellationToken);
        var sourceTypes = ExtractSourceTypes(syntaxTrees, cancellationToken);
        var metadataReferences = CreateMetadataReferences(projectInfo, projectReferences);
        var compilationReferences = projectReferences
            .Select(reference => reference.Compilation.ToMetadataReference())
            .ToArray();

        var compilation = CSharpCompilation.Create(
            projectInfo.AssemblyName,
            syntaxTrees,
            metadataReferences.Concat(compilationReferences),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        return new SourceProjectModel
        {
            ProjectInfo = projectInfo,
            Compilation = compilation,
            ReferencedProjects = projectReferences,
            SourceTypes = sourceTypes
        };
    }

    private async Task<IReadOnlyList<SourceProjectModel>> LoadProjectReferencesAsync(
        IReadOnlyList<string> projectReferences,
        CancellationToken cancellationToken)
    {
        var loaded = new List<SourceProjectModel>();
        foreach (var projectReference in projectReferences)
        {
            loaded.Add(await LoadAsync(projectReference, cancellationToken));
        }

        return loaded;
    }

    private static async Task<IReadOnlyList<SyntaxTree>> LoadSyntaxTreesAsync(
        IReadOnlyList<string> compileFiles,
        CSharpParseOptions parseOptions,
        CancellationToken cancellationToken)
    {
        var trees = new List<SyntaxTree>();

        foreach (var sourceFile in compileFiles.Where(File.Exists))
        {
            var sourceText = await File.ReadAllTextAsync(sourceFile, cancellationToken);
            trees.Add(CSharpSyntaxTree.ParseText(sourceText, parseOptions, sourceFile));
        }

        return trees;
    }

    private static IReadOnlyList<MetadataReference> CreateMetadataReferences(
        MsBuildProjectInfo projectInfo,
        IReadOnlyList<SourceProjectModel> projectReferences)
    {
        var projectReferenceAssemblyNames = new HashSet<string>(
            projectReferences.Select(reference => reference.ProjectInfo.AssemblyName),
            StringComparer.OrdinalIgnoreCase);
        var referencePaths = projectInfo.ReferencePaths
            .Concat(GetRuntimeMetadataReferencePaths())
            .Where(File.Exists)
            .Where(referencePath => !projectReferenceAssemblyNames.Contains(Path.GetFileNameWithoutExtension(referencePath)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return referencePaths
            .Select(referencePath => MetadataReference.CreateFromFile(referencePath))
            .ToArray();
    }

    private static IReadOnlyList<SourceTypeDeclaration> ExtractSourceTypes(
        IReadOnlyList<SyntaxTree> syntaxTrees,
        CancellationToken cancellationToken)
    {
        var sourceTypes = new List<SourceTypeDeclaration>();

        foreach (var syntaxTree in syntaxTrees)
        {
            var root = syntaxTree.GetRoot(cancellationToken);
            foreach (var classDeclaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var attributeData = ReadAttributeData(classDeclaration.AttributeLists);
                sourceTypes.Add(new SourceTypeDeclaration
                {
                    Name = classDeclaration.Identifier.ValueText,
                    FullName = BuildFullTypeName(classDeclaration),
                    IsWorkflowActionResolver = attributeData.AttributeNames.Any(IsWorkflowActionResolverAttribute),
                    ScanAssemblyMarkers = attributeData.ScanAssemblyMarkers,
                    WorkflowActions = attributeData.WorkflowActions
                });
            }
        }

        return sourceTypes;
    }

    private static (List<string> AttributeNames, List<string> ScanAssemblyMarkers, List<string> WorkflowActions) ReadAttributeData(SyntaxList<AttributeListSyntax> attributeLists)
    {
        var attributeNames = new List<string>();
        var scanAssemblyMarkers = new List<string>();
        var workflowActions = new List<string>();

        foreach (var attribute in attributeLists.SelectMany(list => list.Attributes))
        {
            var attributeName = attribute.Name.ToString();
            attributeNames.Add(attributeName);

            if (IsScanAssemblyAttribute(attributeName)
                && attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression is TypeOfExpressionSyntax markerType)
            {
                scanAssemblyMarkers.Add(markerType.Type.ToString());
            }
            else if (IsScanAssemblyAttribute(attributeName)
                && attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax assemblyLiteral
                && assemblyLiteral.IsKind(SyntaxKind.StringLiteralExpression))
            {
                scanAssemblyMarkers.Add(SourceTypeDeclaration.AssemblyReferencePrefix + assemblyLiteral.Token.ValueText);
            }

            if (IsWorkflowActionAttribute(attributeName)
                && attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax literal
                && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                workflowActions.Add(literal.Token.ValueText);
            }
        }

        return (attributeNames, scanAssemblyMarkers, workflowActions);
    }

    private static bool IsWorkflowActionResolverAttribute(string attributeName)
        => AttributeNameMatches(attributeName, "WorkflowActionResolver");

    private static bool IsScanAssemblyAttribute(string attributeName)
        => AttributeNameMatches(attributeName, "ScanAssembly");

    private static bool IsWorkflowActionAttribute(string attributeName)
        => AttributeNameMatches(attributeName, "WorkflowAction");

    private static bool AttributeNameMatches(string candidate, string expected)
    {
        return string.Equals(candidate, expected, StringComparison.Ordinal)
            || string.Equals(candidate, expected + "Attribute", StringComparison.Ordinal)
            || candidate.EndsWith("." + expected, StringComparison.Ordinal)
            || candidate.EndsWith("." + expected + "Attribute", StringComparison.Ordinal);
    }

    private static string BuildFullTypeName(ClassDeclarationSyntax declaration)
    {
        var parts = new Stack<string>();
        parts.Push(declaration.Identifier.ValueText);

        for (SyntaxNode? current = declaration.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case NamespaceDeclarationSyntax namespaceDeclaration:
                    parts.Push(namespaceDeclaration.Name.ToString());
                    break;
                case FileScopedNamespaceDeclarationSyntax fileScopedNamespaceDeclaration:
                    parts.Push(fileScopedNamespaceDeclaration.Name.ToString());
                    break;
                case ClassDeclarationSyntax parentClass:
                    parts.Push(parentClass.Identifier.ValueText);
                    break;
            }
        }

        return string.Join(".", parts);
    }

    private static IReadOnlyList<string> GetRuntimeMetadataReferencePaths()
    {
        var trustedPlatformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrWhiteSpace(trustedPlatformAssemblies))
        {
            return [];
        }

        return trustedPlatformAssemblies
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(File.Exists)
            .ToArray();
    }
}
