using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Xml.Linq;
using Microsoft.Build.Evaluation;
using Microsoft.CodeAnalysis.CSharp;

namespace Reimaginate.Orchestrator.SourceGenerator;

public sealed class MsBuildProjectInfo
{
    public string ProjectPath { get; set; } = string.Empty;

    public string AssemblyName { get; set; } = string.Empty;

    public string TargetFramework { get; set; } = string.Empty;

    public string LanguageVersion { get; set; } = string.Empty;

    public string ProjectExtensionsPath { get; set; } = string.Empty;

    public IReadOnlyList<string> CompileFiles { get; set; } = Array.Empty<string>();

    public IReadOnlyList<string> ProjectReferences { get; set; } = Array.Empty<string>();

    public IReadOnlyList<string> ReferencePaths { get; set; } = Array.Empty<string>();
}

public sealed class MsBuildProjectLoadOptions
{
    public string? ProjectPath { get; init; }

    public string? Configuration { get; init; }

    public string? TargetFramework { get; init; }

    public string? ProjectExtensionsPath { get; init; }

    public MsBuildProjectLoadOptions ForReferencedProject(string projectPath)
        => new()
        {
            ProjectPath = Path.GetFullPath(projectPath),
            Configuration = Configuration,
            TargetFramework = TargetFramework
        };
}

public static class MsBuildProjectInfoLoader
{
    public static async Task<MsBuildProjectInfo> LoadAsync(
        string projectPath,
        MsBuildProjectLoadOptions? loadOptions = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.FromResult(LoadCore(projectPath, loadOptions));
    }

    private static MsBuildProjectInfo LoadCore(string projectPath, MsBuildProjectLoadOptions? loadOptions)
    {
        var normalizedProjectPath = Path.GetFullPath(projectPath);
        var projectDirectory = Path.GetDirectoryName(normalizedProjectPath) ?? string.Empty;
        var globalProperties = CreateGlobalProperties(loadOptions);

        using var projectCollection = new ProjectCollection(globalProperties);
        var project = projectCollection.LoadProject(normalizedProjectPath, globalProperties, toolsVersion: null);

        var targetFramework = project.GetPropertyValue("TargetFramework");
        if (string.IsNullOrWhiteSpace(targetFramework))
        {
            var targetFrameworks = ReadTargetFrameworks(project);
            if (targetFrameworks.Count > 0)
            {
                var selectedTargetFramework = SelectTargetFramework(targetFrameworks, loadOptions?.TargetFramework);
                if (!string.Equals(selectedTargetFramework, loadOptions?.TargetFramework, StringComparison.OrdinalIgnoreCase))
                {
                    var effectiveLoadOptions = loadOptions is null
                        ? new MsBuildProjectLoadOptions
                        {
                            ProjectPath = normalizedProjectPath,
                            TargetFramework = selectedTargetFramework
                        }
                        : new MsBuildProjectLoadOptions
                        {
                            ProjectPath = normalizedProjectPath,
                            Configuration = loadOptions.Configuration,
                            TargetFramework = selectedTargetFramework,
                            ProjectExtensionsPath = loadOptions.ProjectExtensionsPath
                        };
                    return LoadCore(
                        normalizedProjectPath,
                        effectiveLoadOptions);
                }
            }

            throw new InvalidOperationException($"Could not determine TargetFramework for '{normalizedProjectPath}'.");
        }

        var projectExtensionsPath = loadOptions?.ProjectExtensionsPath;
        if (string.IsNullOrWhiteSpace(projectExtensionsPath))
        {
            projectExtensionsPath = project.GetPropertyValue("MSBuildProjectExtensionsPath");
        }

        if (string.IsNullOrWhiteSpace(projectExtensionsPath))
        {
            projectExtensionsPath = Path.Combine(projectDirectory, "obj");
        }
        else
        {
            projectExtensionsPath = GetFullPath(projectDirectory, projectExtensionsPath);
        }

        var referencePaths = ReadHintPathReferences(normalizedProjectPath)
            .Concat(ReadPackageCompileReferences(
                Path.Combine(Path.GetFullPath(projectExtensionsPath), "project.assets.json"),
                targetFramework))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new MsBuildProjectInfo
        {
            ProjectPath = normalizedProjectPath,
            AssemblyName = project.GetPropertyValue("AssemblyName") ?? Path.GetFileNameWithoutExtension(normalizedProjectPath),
            TargetFramework = targetFramework,
            LanguageVersion = project.GetPropertyValue("LangVersion") ?? string.Empty,
            ProjectExtensionsPath = Path.GetFullPath(projectExtensionsPath),
            CompileFiles = ReadItemPaths(project, "Compile"),
            ProjectReferences = ReadItemPaths(project, "ProjectReference"),
            ReferencePaths = referencePaths
        };
    }

    internal static LanguageVersion ParseLanguageVersion(string? languageVersion)
    {
        if (string.IsNullOrWhiteSpace(languageVersion))
        {
            return LanguageVersion.Default;
        }

        return languageVersion.Trim().ToLowerInvariant() switch
        {
            "default" => LanguageVersion.Default,
            "latest" => LanguageVersion.Latest,
            "latestmajor" => LanguageVersion.LatestMajor,
            "preview" => LanguageVersion.Preview,
            "1" or "1.0" or "iso-1" => LanguageVersion.CSharp1,
            "2" or "2.0" or "iso-2" => LanguageVersion.CSharp2,
            "3" or "3.0" => LanguageVersion.CSharp3,
            "4" or "4.0" => LanguageVersion.CSharp4,
            "5" or "5.0" => LanguageVersion.CSharp5,
            "6" or "6.0" => LanguageVersion.CSharp6,
            "7" or "7.0" => LanguageVersion.CSharp7,
            "7.1" => LanguageVersion.CSharp7_1,
            "7.2" => LanguageVersion.CSharp7_2,
            "7.3" => LanguageVersion.CSharp7_3,
            "8" or "8.0" => LanguageVersion.CSharp8,
            "9" or "9.0" => LanguageVersion.CSharp9,
            "10" or "10.0" => LanguageVersion.CSharp10,
            "11" or "11.0" => LanguageVersion.CSharp11,
            "12" or "12.0" => LanguageVersion.CSharp12,
            "13" or "13.0" => LanguageVersion.CSharp13,
            "14" or "14.0" => LanguageVersion.CSharp14,
            _ => LanguageVersion.Default
        };
    }

    private static Dictionary<string, string> CreateGlobalProperties(MsBuildProjectLoadOptions? loadOptions)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DesignTimeBuild"] = "true",
            ["OrchestratorEmitWorkflowActionCatalog"] = "false"
        };

        if (!string.IsNullOrWhiteSpace(loadOptions?.Configuration))
        {
            properties["Configuration"] = loadOptions.Configuration!;
        }

        if (!string.IsNullOrWhiteSpace(loadOptions?.TargetFramework))
        {
            properties["TargetFramework"] = loadOptions.TargetFramework!;
        }

        if (!string.IsNullOrWhiteSpace(loadOptions?.ProjectExtensionsPath))
        {
            properties["MSBuildProjectExtensionsPath"] = loadOptions.ProjectExtensionsPath!;
        }

        return properties;
    }

    private static IReadOnlyList<string> ReadTargetFrameworks(Project project)
    {
        var targetFrameworks = project.GetPropertyValue("TargetFrameworks");
        if (string.IsNullOrWhiteSpace(targetFrameworks))
        {
            return [];
        }

        return targetFrameworks
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    private static string SelectTargetFramework(IReadOnlyList<string> targetFrameworks, string? requestedTargetFramework)
    {
        if (!string.IsNullOrWhiteSpace(requestedTargetFramework))
        {
            var matchingTargetFramework = targetFrameworks.FirstOrDefault(targetFramework =>
                string.Equals(targetFramework, requestedTargetFramework, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(matchingTargetFramework))
            {
                return matchingTargetFramework;
            }
        }

        return targetFrameworks[0];
    }

    private static IReadOnlyList<string> ReadItemPaths(Project project, string itemName)
    {
        return project.GetItems(itemName)
            .Select(item =>
            {
                var fullPath = item.GetMetadataValue("FullPath");
                return string.IsNullOrWhiteSpace(fullPath)
                    ? GetFullPath(Path.GetDirectoryName(project.FullPath) ?? string.Empty, item.EvaluatedInclude)
                    : Path.GetFullPath(fullPath);
            })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string GetFullPath(string projectDirectory, string path)
    {
        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(projectDirectory, path));
    }

    private static IReadOnlyList<string> ReadHintPathReferences(string projectPath)
    {
        try
        {
            var projectDirectory = Path.GetDirectoryName(projectPath) ?? string.Empty;
            var document = XDocument.Load(projectPath, LoadOptions.None);

            return document
                .Descendants()
                .Where(element => string.Equals(element.Name.LocalName, "Reference", StringComparison.Ordinal))
                .Elements()
                .Where(element => string.Equals(element.Name.LocalName, "HintPath", StringComparison.Ordinal))
                .Select(element => element.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => Path.GetFullPath(Path.Combine(projectDirectory, value)))
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    internal static IReadOnlyList<string> ReadPackageCompileReferences(string assetsFilePath, string targetFramework)
    {
        try
        {
            if (!File.Exists(assetsFilePath))
            {
                return [];
            }

            using var document = JsonDocument.Parse(File.ReadAllText(assetsFilePath));
            var root = document.RootElement;
            if (!TryGetTarget(root, targetFramework, out var targetElement))
            {
                return [];
            }

            var packageFolders = ReadPackageFolders(root);
            if (packageFolders.Count == 0)
            {
                return [];
            }

            if (!root.TryGetProperty("libraries", out var librariesElement)
                || librariesElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var resolvedReferences = new List<string>();
            foreach (var library in targetElement.EnumerateObject())
            {
                if (library.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!library.Value.TryGetProperty("compile", out var compileElement)
                    || compileElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!librariesElement.TryGetProperty(library.Name, out var libraryMetadata)
                    || libraryMetadata.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var libraryType = GetStringProperty(libraryMetadata, "type");
                if (!string.Equals(libraryType, "package", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var libraryPath = GetStringProperty(libraryMetadata, "path");
                if (string.IsNullOrWhiteSpace(libraryPath))
                {
                    continue;
                }

                foreach (var compileReference in compileElement.EnumerateObject())
                {
                    foreach (var packageFolder in packageFolders)
                    {
                        var candidatePath = Path.GetFullPath(Path.Combine(packageFolder, libraryPath, compileReference.Name));
                        if (!File.Exists(candidatePath))
                        {
                            continue;
                        }

                        resolvedReferences.Add(candidatePath);
                        break;
                    }
                }
            }

            return resolvedReferences
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static bool TryGetTarget(JsonElement root, string targetFramework, out JsonElement targetElement)
    {
        targetElement = default;

        if (!root.TryGetProperty("targets", out var targetsElement)
            || targetsElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (targetsElement.TryGetProperty(targetFramework, out targetElement)
            && targetElement.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        foreach (var target in targetsElement.EnumerateObject())
        {
            if (!target.Name.StartsWith(targetFramework + "/", StringComparison.OrdinalIgnoreCase)
                || target.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            targetElement = target.Value;
            return true;
        }

        return false;
    }

    private static IReadOnlyList<string> ReadPackageFolders(JsonElement root)
    {
        var packageFolders = new List<string>();

        if (root.TryGetProperty("packageFolders", out var packageFoldersElement)
            && packageFoldersElement.ValueKind == JsonValueKind.Object)
        {
            packageFolders.AddRange(packageFoldersElement
                .EnumerateObject()
                .Select(folder => folder.Name)
                .Where(folder => !string.IsNullOrWhiteSpace(folder)));
        }

        if (root.TryGetProperty("project", out var projectElement)
            && projectElement.ValueKind == JsonValueKind.Object
            && projectElement.TryGetProperty("restore", out var restoreElement)
            && restoreElement.ValueKind == JsonValueKind.Object)
        {
            var packagesPath = GetStringProperty(restoreElement, "packagesPath");
            if (!string.IsNullOrWhiteSpace(packagesPath))
            {
                packageFolders.Add(packagesPath);
            }
        }

        return packageFolders
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? GetStringProperty(JsonElement properties, string name)
    {
        return properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out var value)
            ? value.GetString()
            : null;
    }
}
