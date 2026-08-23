using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Reimaginate.Orchestrator.SourceGenerator;

public static class ProjectDiscovery
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        "bin",
        "node_modules",
        "obj"
    };

    public static string FindRepoRoot(string projectDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(projectDirectory));
        DirectoryInfo? solutionFallback = null;

        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }

            if (solutionFallback is null && current.EnumerateFiles("*.sln").Any())
            {
                solutionFallback = current;
            }

            current = current.Parent;
        }

        return solutionFallback?.FullName ?? Path.GetFullPath(projectDirectory);
    }

    public static IReadOnlyList<string> ReadProjectReferences(string projectPath)
    {
        try
        {
            var document = XDocument.Load(projectPath, LoadOptions.None);
            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? string.Empty;

            return document
                .Descendants()
                .Where(element => string.Equals(element.Name.LocalName, "ProjectReference", StringComparison.Ordinal))
                .Select(element => element.Attribute("Include")?.Value)
                .Where(include => !string.IsNullOrWhiteSpace(include))
                .Select(include => Path.GetFullPath(Path.Combine(projectDirectory, include!)))
                .Distinct(PathComparer)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    public static bool ContainsWorkflowFiles(string projectPath)
    {
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
        return !string.IsNullOrWhiteSpace(projectDirectory) && ContainsWorkflowFiles(new DirectoryInfo(projectDirectory));
    }

    private static bool ContainsWorkflowFiles(DirectoryInfo directory)
    {
        if (IgnoredDirectories.Contains(directory.Name))
        {
            return false;
        }

        try
        {
            if (directory.EnumerateFiles("*.workflow.yaml").Any() || directory.EnumerateFiles("*.workflow.yml").Any())
            {
                return true;
            }
        }
        catch
        {
            return false;
        }

        try
        {
            foreach (var child in directory.EnumerateDirectories())
            {
                if (ContainsWorkflowFiles(child))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }
}
