using Azure.Storage.Blobs;
using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.AzureStorage;

public sealed class AzureBlobStorageCheckpointStorage(BlobContainerClient containerClient, string? blobPrefix = null) : ICheckpointStorage
{

    private readonly BlobContainerClient _containerClient = containerClient ?? throw new ArgumentNullException(nameof(containerClient));
    private readonly string _blobPrefix = (blobPrefix ?? string.Empty).Trim('/');

    public Microsoft.Agents.AI.Workflows.Checkpointing.JsonCheckpointStore CreateStore()
    {
        _containerClient.CreateIfNotExists();
        return new AzureBlobStorageJsonCheckpointStore(_containerClient, _blobPrefix);
    }
}
