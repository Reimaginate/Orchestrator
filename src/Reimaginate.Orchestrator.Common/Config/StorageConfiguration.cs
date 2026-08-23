using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.Common.Stores.Checkpoints;
using Reimaginate.Orchestrator.Common.Stores.Instances;
using Reimaginate.Orchestrator.Common.Stores.WorkflowDefinitions;

namespace Reimaginate.Orchestrator.Common.Config;

public static class StorageConfiguration
{
    public static IServiceCollection AddConfiguredOrchestratorStorage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.RemoveAll<ICheckpointStorage>();
        services.AddSingleton<ICheckpointStorage>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<OrchestratorOptions>>().Value;
            if (options.WorkflowCheckpointStorage.TryResolveFileSystemRootPath(out var folderPath))
            {
                return new FileSystemCheckpointStorage(new DirectoryInfo(folderPath!));
            }

            return sp.GetRequiredService<InMemoryCheckpointStorage>();
        });

        services.RemoveAll<IWorkflowInstanceStore>();
        services.AddSingleton<IWorkflowInstanceStore>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<OrchestratorOptions>>().Value;
            if (options.WorkflowInstanceStorage.TryResolveFileSystemRootPath(out var folderPath))
            {
                return new FileSystemWorkflowInstanceStore(new DirectoryInfo(folderPath!));
            }

            return sp.GetRequiredService<InMemoryWorkflowInstanceStore>();
        });

        services.RemoveAll<IWorkflowDefinitionStore>();
        services.AddSingleton<IWorkflowDefinitionStore>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<OrchestratorOptions>>().Value;
            if (options.WorkflowDefinitionStorage.TryResolveFileSystemRootPath(out var folderPath, WorkflowDefinitionStorageDefaults.DefaultFileSystemFolderName))
            {
                return new FileSystemWorkflowDefinitionStore(new DirectoryInfo(folderPath!));
            }

            return sp.GetRequiredService<InMemoryWorkflowDefinitionStore>();
        });

        return services;
    }
}
