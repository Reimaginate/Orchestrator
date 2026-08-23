using System.Text.Json;
using Reimaginate.Orchestrator.SourceGenerator;

namespace Reimaginate.Orchestrator.SourceGenerator.CatalogTool;

public sealed class WorkflowActionCatalogWriter
{
    private readonly CatalogFileStore catalogFileStore = new();
    private readonly SourceProjectModelLoader projectModelLoader;
    private readonly MsBuildProjectLoadOptions? loadOptions;

    public WorkflowActionCatalogWriter(MsBuildProjectLoadOptions? loadOptions = null)
    {
        this.loadOptions = loadOptions;
        projectModelLoader = new SourceProjectModelLoader(loadOptions);
    }

    public async Task<CatalogWriteResult> WriteAsync(
        string projectPath,
        string? catalogOutputPath = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedProjectPath = Path.GetFullPath(projectPath);
        var hostProjectModel = await projectModelLoader.LoadAsync(normalizedProjectPath, cancellationToken);
        var hostProjectInfo = hostProjectModel.ProjectInfo;
        var repoRoot = ProjectDiscovery.FindRepoRoot(Path.GetDirectoryName(normalizedProjectPath) ?? Directory.GetCurrentDirectory());
        var catalogPath = GetCatalogPath(normalizedProjectPath, hostProjectInfo.AssemblyName, catalogOutputPath);
        var discovery = WorkflowActionSymbolDiscovery.Discover(hostProjectModel);

        if (!discovery.HasResolvers)
        {
            return await catalogFileStore.DeleteCatalogIfExistsAsync(catalogPath, cancellationToken);
        }

        var workflowProjects = await LoadWorkflowProjectsAsync(hostProjectInfo.ProjectReferences, cancellationToken);
        var catalog = discovery.Success
            ? CreateSuccessCatalog(repoRoot, normalizedProjectPath, hostProjectInfo.AssemblyName, workflowProjects, discovery)
            : CreateUnresolvedCatalog(repoRoot, normalizedProjectPath, hostProjectInfo.AssemblyName, workflowProjects, discovery);
        var serializedCatalog = JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true });

        Directory.CreateDirectory(Path.GetDirectoryName(catalogPath)!);
        return await catalogFileStore.WriteCatalogAsync(catalogPath, serializedCatalog, cancellationToken);
    }

    private async Task<IReadOnlyList<WorkflowProjectReference>> LoadWorkflowProjectsAsync(
        IReadOnlyList<string> directProjectReferences,
        CancellationToken cancellationToken)
    {
        var workflowProjects = new List<WorkflowProjectReference>();

        foreach (var reference in directProjectReferences)
        {
            if (!ProjectDiscovery.ContainsWorkflowFiles(reference))
            {
                continue;
            }

            var projectInfo = await MsBuildProjectInfoLoader.LoadAsync(
                reference,
                loadOptions?.ForReferencedProject(reference),
                cancellationToken);
            workflowProjects.Add(new WorkflowProjectReference(
                projectInfo.ProjectPath,
                projectInfo.AssemblyName));
        }

        return workflowProjects
            .GroupBy(workflow => workflow.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static string GetCatalogPath(string projectPath, string assemblyName, string? catalogOutputPath)
    {
        if (!string.IsNullOrWhiteSpace(catalogOutputPath))
        {
            return Path.GetFullPath(catalogOutputPath);
        }

        var projectDirectory = Path.GetDirectoryName(projectPath) ?? Directory.GetCurrentDirectory();
        return Path.Combine(projectDirectory, "obj", "orchestrator", "workflow-actions", assemblyName + ".json");
    }

    private static WorkflowActionCatalog CreateSuccessCatalog(
        string repoRoot,
        string hostProjectPath,
        string hostAssemblyName,
        IReadOnlyList<WorkflowProjectReference> workflowProjects,
        WorkflowActionDiscoveryResult discovery)
    {
        return new WorkflowActionCatalog
        {
            RepoRoot = repoRoot,
            HostProjectPath = hostProjectPath,
            HostAssemblyName = hostAssemblyName,
            Status = "success",
            ResolverTypes = discovery.ResolverTypes,
            Workflows = workflowProjects
                .Select(workflow => new WorkflowActionCatalogWorkflow
                {
                    ProjectPath = workflow.ProjectPath,
                    AssemblyName = workflow.AssemblyName,
                    Status = "success",
                    Actions = discovery.Actions
                        .Select(action => new WorkflowActionCatalogAction
                        {
                            Name = action.Name,
                            RequestType = action.RequestType,
                            ResponseType = action.ResponseType,
                            RequestProperties = action.RequestProperties,
                            ResponseProperties = action.ResponseProperties
                        })
                        .ToArray()
                })
                .ToArray()
        };
    }

    private static WorkflowActionCatalog CreateUnresolvedCatalog(
        string repoRoot,
        string hostProjectPath,
        string hostAssemblyName,
        IReadOnlyList<WorkflowProjectReference> workflowProjects,
        WorkflowActionDiscoveryResult discovery)
    {
        return new WorkflowActionCatalog
        {
            RepoRoot = repoRoot,
            HostProjectPath = hostProjectPath,
            HostAssemblyName = hostAssemblyName,
            Status = "unresolved",
            Reason = discovery.Reason,
            ResolverTypes = discovery.ResolverTypes,
            Workflows = workflowProjects
                .Select(workflow => new WorkflowActionCatalogWorkflow
                {
                    ProjectPath = workflow.ProjectPath,
                    AssemblyName = workflow.AssemblyName,
                    Status = "unresolved",
                    Reason = discovery.Reason
                })
                .ToArray()
        };
    }

    private sealed class WorkflowProjectReference
    {
        public WorkflowProjectReference(string projectPath, string assemblyName)
        {
            ProjectPath = projectPath;
            AssemblyName = assemblyName;
        }

        public string ProjectPath { get; }

        public string AssemblyName { get; }
    }
}

public sealed class CatalogWriteResult
{
    public string? WarningCode { get; init; }

    public string? WarningMessage { get; init; }

    public static CatalogWriteResult Success() => new();

    public static CatalogWriteResult Warning(string code, string message)
        => new()
        {
            WarningCode = code,
            WarningMessage = message
        };
}
