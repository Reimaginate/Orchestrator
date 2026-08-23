using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.AzureStorage;

public static class ConfigureServices
{
    public static IServiceCollection AddAzureBlobCheckpointStorage(this IServiceCollection services, BlobContainerClient containerClient, string? blobPrefix = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(containerClient);

        services.RemoveAll<ICheckpointStorage>();
        services.AddSingleton<ICheckpointStorage>(_ => new AzureBlobStorageCheckpointStorage(containerClient, blobPrefix));
        return services;
    }

    public static IServiceCollection AddAzureBlobCheckpointStorage(this IServiceCollection services, string connectionString, string containerName, string? blobPrefix = null, BlobClientOptions? clientOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        return services.AddAzureBlobCheckpointStorage(new BlobContainerClient(connectionString, containerName, clientOptions), blobPrefix);
    }

    public static IServiceCollection AddAzureBlobWorkflowDefinitionStore(this IServiceCollection services, BlobContainerClient containerClient, string? blobPrefix = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(containerClient);

        services.RemoveAll<IWorkflowDefinitionStore>();
        services.AddSingleton<IWorkflowDefinitionStore>(_ => new AzureBlobStorageWorkflowDefinitionStore(containerClient, blobPrefix));
        return services;
    }

    public static IServiceCollection AddAzureBlobWorkflowDefinitionStore(this IServiceCollection services, string connectionString, string containerName, string? blobPrefix = null, BlobClientOptions? clientOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        return services.AddAzureBlobWorkflowDefinitionStore(new BlobContainerClient(connectionString, containerName, clientOptions), blobPrefix);
    }

    public static IServiceCollection AddAzureBlobWorkflowInstanceStore(this IServiceCollection services, BlobContainerClient containerClient, string? blobPrefix = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(containerClient);

        services.RemoveAll<IWorkflowInstanceStore>();
        services.AddSingleton<IWorkflowInstanceStore>(_ => new AzureBlobStorageWorkflowInstanceStore(containerClient, blobPrefix));
        return services;
    }

    public static IServiceCollection AddAzureBlobWorkflowInstanceStore(this IServiceCollection services, string connectionString, string containerName, string? blobPrefix = null, BlobClientOptions? clientOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        return services.AddAzureBlobWorkflowInstanceStore(new BlobContainerClient(connectionString, containerName, clientOptions), blobPrefix);
    }
}
