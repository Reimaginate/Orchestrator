using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.AzureStorage;

public sealed class AzureBlobStorageWorkflowInstanceStore(BlobContainerClient containerClient, string? blobPrefix = null) : IWorkflowInstanceStore
{
    #region Fields

    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    #endregion

    #region Public API

    public async Task<WorkflowInstanceGetResult> GetAsync(string workflowInstanceId, CancellationToken cancellationToken)
    {
        try
        {
            // Resolve the blob path once and deserialize a normalized instance payload.
            var blobClient = containerClient.GetBlobClient(GetBlobName(workflowInstanceId));

            try
            {
                var response = await blobClient.DownloadContentAsync(cancellationToken);
                var workflowInstance = response.Value.Content.ToObjectFromJson<WorkflowInstance>(jsonOptions);

                if (workflowInstance == null)
                {
                    return new WorkflowInstanceGetResult
                    {
                        Success = false,
                        FailureReason = $"Workflow instance '{workflowInstanceId}' could not be deserialized from blob '{blobClient.Name}'."
                    };
                }

                return new WorkflowInstanceGetResult
                {
                    Success = true,
                    WorkflowInstance = NormalizeWorkflowInstance(workflowInstance, response.Value.Details.ETag.ToString())
                };
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return new WorkflowInstanceGetResult
                {
                    Success = true,
                    NotFound = true
                };
            }
        }
        catch (Exception ex)
        {
            return new WorkflowInstanceGetResult
            {
                Success = false,
                FailureReason = ex.Message
            };
        }
    }

    public async Task<WorkflowInstanceUpsertResult> UpsertAsync(WorkflowInstance workflowInstance, CancellationToken cancellationToken)
    {
        try
        {
            // Ensure the container exists before writing workflow state.
            await containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

            var blobClient = containerClient.GetBlobClient(GetBlobName(workflowInstance.Id));

            // Persist a normalized shape to avoid null-collection handling at read time.
            var normalizedWorkflowInstance = NormalizeWorkflowInstance(workflowInstance);
            var response = await blobClient.UploadAsync(
                BinaryData.FromString(JsonSerializer.Serialize(normalizedWorkflowInstance, jsonOptions)),
                overwrite: true,
                cancellationToken: cancellationToken);

            return new WorkflowInstanceUpsertResult
            {
                Success = true,
                ConcurrencyToken = response.Value.ETag.ToString()
            };
        }
        catch (Exception ex)
        {
            return new WorkflowInstanceUpsertResult
            {
                Success = false,
                FailureReason = ex.Message
            };
        }
    }

    public async Task<WorkflowInstanceWriteResult> CreateAsync(WorkflowInstance workflowInstance, CancellationToken cancellationToken)
    {
        try
        {
            await containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

            var blobClient = containerClient.GetBlobClient(GetBlobName(workflowInstance.Id));
            var normalizedWorkflowInstance = NormalizeWorkflowInstance(workflowInstance);

            var response = await blobClient.UploadAsync(
                BinaryData.FromString(JsonSerializer.Serialize(normalizedWorkflowInstance, jsonOptions)),
                new BlobUploadOptions
                {
                    Conditions = new BlobRequestConditions
                    {
                        IfNoneMatch = ETag.All
                    }
                },
                cancellationToken);

            return new WorkflowInstanceWriteResult
            {
                Success = true,
                ConcurrencyToken = response.Value.ETag.ToString()
            };
        }
        catch (RequestFailedException ex) when (ex.Status == 409 || ex.Status == 412)
        {
            return new WorkflowInstanceWriteResult
            {
                Success = false,
                Conflict = true
            };
        }
        catch (Exception ex)
        {
            return new WorkflowInstanceWriteResult
            {
                Success = false,
                FailureReason = ex.Message
            };
        }
    }

    public async Task<WorkflowInstanceWriteResult> ReplaceAsync(WorkflowInstance workflowInstance, string expectedConcurrencyToken, CancellationToken cancellationToken)
    {
        try
        {
            await containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

            var blobClient = containerClient.GetBlobClient(GetBlobName(workflowInstance.Id));
            var normalizedWorkflowInstance = NormalizeWorkflowInstance(workflowInstance);

            var response = await blobClient.UploadAsync(
                BinaryData.FromString(JsonSerializer.Serialize(normalizedWorkflowInstance, jsonOptions)),
                new BlobUploadOptions
                {
                    Conditions = new BlobRequestConditions
                    {
                        IfMatch = new ETag(expectedConcurrencyToken)
                    }
                },
                cancellationToken);

            return new WorkflowInstanceWriteResult
            {
                Success = true,
                ConcurrencyToken = response.Value.ETag.ToString()
            };
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return new WorkflowInstanceWriteResult
            {
                Success = false,
                NotFound = true
            };
        }
        catch (RequestFailedException ex) when (ex.Status == 409 || ex.Status == 412)
        {
            return new WorkflowInstanceWriteResult
            {
                Success = false,
                Conflict = true
            };
        }
        catch (Exception ex)
        {
            return new WorkflowInstanceWriteResult
            {
                Success = false,
                FailureReason = ex.Message
            };
        }
    }

    public async Task<WorkflowInstanceFindResult> FindByCorrelationAsync(IReadOnlyCollection<WorkflowResolvedCorrelationKey> correlationKeys, IReadOnlyCollection<string> statuses, CancellationToken cancellationToken)
    {
        try
        {
            // Skip storage scans when the status filter is empty.
            var normalizedStatuses = NormalizeStatuses(statuses);
            if (normalizedStatuses.Count == 0)
            {
                return new WorkflowInstanceFindResult { Success = true, WorkflowInstances = [] };
            }

            var normalizedKeys = correlationKeys
                .Where(x => !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.Value))
                .ToArray();

            var workflowInstances = await FindWorkflowInstancesAsync(
                workflowInstance => normalizedStatuses.Contains(workflowInstance.Status)
                    && HasAllCorrelationKeys(workflowInstance, normalizedKeys),
                cancellationToken);

            return new WorkflowInstanceFindResult
            {
                Success = true,
                WorkflowInstances = workflowInstances
            };
        }
        catch (Exception ex)
        {
            return new WorkflowInstanceFindResult
            {
                Success = false,
                FailureReason = ex.Message
            };
        }
    }

    public async Task<WorkflowInstanceFindResult> FindByOriginatingEventIdAsync(string originatingEventId, string workflowType, IReadOnlyCollection<string> statuses, CancellationToken cancellationToken)
    {
        try
        {
            // Guard against incomplete search criteria.
            if (string.IsNullOrWhiteSpace(originatingEventId) || string.IsNullOrWhiteSpace(workflowType))
            {
                return new WorkflowInstanceFindResult { Success = true, WorkflowInstances = [] };
            }

            var normalizedStatuses = NormalizeStatuses(statuses);
            if (normalizedStatuses.Count == 0)
            {
                return new WorkflowInstanceFindResult { Success = true, WorkflowInstances = [] };
            }

            var workflowInstances = await FindWorkflowInstancesAsync(
                workflowInstance => string.Equals(workflowInstance.OriginatingEventId, originatingEventId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(workflowInstance.WorkflowType, workflowType, StringComparison.OrdinalIgnoreCase)
                    && normalizedStatuses.Contains(workflowInstance.Status),
                cancellationToken);

            return new WorkflowInstanceFindResult
            {
                Success = true,
                WorkflowInstances = workflowInstances
            };
        }
        catch (Exception ex)
        {
            return new WorkflowInstanceFindResult
            {
                Success = false,
                FailureReason = ex.Message
            };
        }
    }

    #endregion

    #region Query helpers

    private async Task<List<WorkflowInstance>> FindWorkflowInstancesAsync(Func<WorkflowInstance, bool> predicate, CancellationToken cancellationToken)
    {
        var results = new List<WorkflowInstance>();
        var prefix = string.IsNullOrWhiteSpace(blobPrefix) ? null : blobPrefix.TrimEnd('/') + "/";

        // Scan JSON blobs under the configured prefix and evaluate each normalized instance.
        await foreach (var blobItem in containerClient.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, cancellationToken))
        {
            if (!blobItem.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var blobClient = containerClient.GetBlobClient(blobItem.Name);
            var response = await blobClient.DownloadContentAsync(cancellationToken);
            var workflowInstance = response.Value.Content.ToObjectFromJson<WorkflowInstance>(jsonOptions);
            if (workflowInstance is null)
            {
                continue;
            }

            var normalized = NormalizeWorkflowInstance(workflowInstance);
            if (predicate(normalized))
            {
                results.Add(normalized);
            }
        }

        return results;
    }

    private static bool HasAllCorrelationKeys(WorkflowInstance workflowInstance, IReadOnlyCollection<WorkflowResolvedCorrelationKey> correlationKeys)
    {
        if (correlationKeys.Count == 0)
        {
            return false;
        }

        // Allow correlation bootstrap for instances that have not recorded keys yet.
        // This enables manually-started workflows to be discovered by their first matching resume event.
        if (workflowInstance.CorrelationKeys.Count == 0)
        {
            return true;
        }

        return correlationKeys.All(key => workflowInstance.CorrelationKeys.Any(existing =>
            string.Equals(existing.Name, key.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.Value, key.Value, StringComparison.OrdinalIgnoreCase)));
    }

    #endregion

    #region Normalization helpers

    private static HashSet<string> NormalizeStatuses(IReadOnlyCollection<string> statuses)
    {
        return statuses
            .Where(status => !string.IsNullOrWhiteSpace(status))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static WorkflowInstance NormalizeWorkflowInstance(WorkflowInstance workflowInstance, string? concurrencyToken = null)
    {
        workflowInstance.Correlations ??= [];
        workflowInstance.CorrelationKeys ??= [];
        workflowInstance.ProcessedEvents ??= [];
        workflowInstance.AwaitingEvents ??= [];
        workflowInstance.Metadata ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        workflowInstance.ConcurrencyToken = concurrencyToken ?? workflowInstance.ConcurrencyToken;
        return workflowInstance;
    }

    #endregion

    #region Blob naming

    private string GetBlobName(string workflowInstanceId)
    {
        // Replace path separators to ensure workflow ids always map to a valid blob filename.
        var sanitizedWorkflowInstanceId = workflowInstanceId.Replace("/", "_", StringComparison.Ordinal).Replace("\\", "_", StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(blobPrefix)
            ? $"{sanitizedWorkflowInstanceId}.json"
            : $"{blobPrefix.TrimEnd('/')}/{sanitizedWorkflowInstanceId}.json";
    }

    #endregion
}
