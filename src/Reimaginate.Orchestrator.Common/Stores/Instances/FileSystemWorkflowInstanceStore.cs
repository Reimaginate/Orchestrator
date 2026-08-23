using System.Text.Json;
using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.Common.Stores.Instances;

public sealed class FileSystemWorkflowInstanceStore(DirectoryInfo rootDirectory) : IWorkflowInstanceStore
{
    #region Serialization

    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    #endregion

    #region Public API

    public async Task<WorkflowInstanceGetResult> GetAsync(string workflowInstanceId, CancellationToken cancellationToken)
    {
        try
        {
            // Resolve the instance file for the id and short-circuit when it does not exist.
            var file = GetFile(workflowInstanceId);
            if (!file.Exists)
            {
                return new WorkflowInstanceGetResult
                {
                    Success = true,
                    NotFound = true
                };
            }

            await using var stream = file.OpenRead();
            var workflowInstance = await JsonSerializer.DeserializeAsync<WorkflowInstance>(stream, jsonOptions, cancellationToken);

            // Return a clear failure when persisted JSON cannot be materialized into a workflow instance.
            if (workflowInstance == null)
            {
                return new WorkflowInstanceGetResult
                {
                    Success = false,
                    FailureReason = $"Workflow instance '{workflowInstanceId}' could not be deserialized from '{file.FullName}'."
                };
            }

            return new WorkflowInstanceGetResult
            {
                Success = true,
                WorkflowInstance = NormalizeWorkflowInstance(workflowInstance)
            };
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
            // Ensure the target directory exists before writing and always persist normalized collections.
            var file = GetFile(workflowInstance.Id);
            file.Directory?.Create();

            var normalizedWorkflowInstance = NormalizeWorkflowInstance(workflowInstance);
            normalizedWorkflowInstance.ConcurrencyToken = CreateConcurrencyToken();
            await WriteAsync(file, normalizedWorkflowInstance, cancellationToken);

            return new WorkflowInstanceUpsertResult
            {
                Success = true,
                ConcurrencyToken = normalizedWorkflowInstance.ConcurrencyToken
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
            var file = GetFile(workflowInstance.Id);
            file.Directory?.Create();

            if (file.Exists)
            {
                return new WorkflowInstanceWriteResult
                {
                    Success = false,
                    Conflict = true
                };
            }

            var normalizedWorkflowInstance = NormalizeWorkflowInstance(workflowInstance);
            normalizedWorkflowInstance.ConcurrencyToken = CreateConcurrencyToken();

            await using var stream = file.Open(FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(stream, normalizedWorkflowInstance, jsonOptions, cancellationToken);

            return new WorkflowInstanceWriteResult
            {
                Success = true,
                ConcurrencyToken = normalizedWorkflowInstance.ConcurrencyToken
            };
        }
        catch (IOException)
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
            var file = GetFile(workflowInstance.Id);
            if (!file.Exists)
            {
                return new WorkflowInstanceWriteResult
                {
                    Success = false,
                    NotFound = true
                };
            }

            var current = await ReadAsync(file, cancellationToken);
            if (current is null)
            {
                return new WorkflowInstanceWriteResult
                {
                    Success = false,
                    FailureReason = $"Workflow instance '{workflowInstance.Id}' could not be deserialized from '{file.FullName}'."
                };
            }

            if (!string.Equals(current.ConcurrencyToken, expectedConcurrencyToken, StringComparison.Ordinal))
            {
                return new WorkflowInstanceWriteResult
                {
                    Success = false,
                    Conflict = true
                };
            }

            var normalizedWorkflowInstance = NormalizeWorkflowInstance(workflowInstance);
            normalizedWorkflowInstance.ConcurrencyToken = CreateConcurrencyToken();
            await WriteAsync(file, normalizedWorkflowInstance, cancellationToken);

            return new WorkflowInstanceWriteResult
            {
                Success = true,
                ConcurrencyToken = normalizedWorkflowInstance.ConcurrencyToken
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

    public Task<WorkflowInstanceFindResult> FindByCorrelationAsync(IReadOnlyCollection<WorkflowResolvedCorrelationKey> correlationKeys, IReadOnlyCollection<string> statuses, CancellationToken cancellationToken)
    {
        // Filter invalid statuses first so callers can safely pass mixed or empty status values.
        var normalizedStatuses = NormalizeStatuses(statuses);
        if (normalizedStatuses.Count == 0)
        {
            return Task.FromResult(new WorkflowInstanceFindResult { Success = true, WorkflowInstances = [] });
        }

        // Ignore correlation keys with missing name/value pairs before matching.
        var normalizedKeys = correlationKeys
            .Where(x => !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.Value))
            .ToArray();

        return FindAsync(workflowInstance =>
            normalizedStatuses.Contains(workflowInstance.Status)
            && HasAllCorrelationKeys(workflowInstance, normalizedKeys), cancellationToken);
    }

    public Task<WorkflowInstanceFindResult> FindByOriginatingEventIdAsync(string originatingEventId, string workflowType, IReadOnlyCollection<string> statuses, CancellationToken cancellationToken)
    {
        // Originating event queries require both event id and workflow type.
        if (string.IsNullOrWhiteSpace(originatingEventId) || string.IsNullOrWhiteSpace(workflowType))
        {
            return Task.FromResult(new WorkflowInstanceFindResult { Success = true, WorkflowInstances = [] });
        }

        var normalizedStatuses = NormalizeStatuses(statuses);
        if (normalizedStatuses.Count == 0)
        {
            return Task.FromResult(new WorkflowInstanceFindResult { Success = true, WorkflowInstances = [] });
        }

        return FindAsync(workflowInstance =>
            string.Equals(workflowInstance.OriginatingEventId, originatingEventId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(workflowInstance.WorkflowType, workflowType, StringComparison.OrdinalIgnoreCase)
            && normalizedStatuses.Contains(workflowInstance.Status), cancellationToken);
    }

    #endregion

    #region Search helpers

    private async Task<WorkflowInstanceFindResult> FindAsync(Func<WorkflowInstance, bool> predicate, CancellationToken cancellationToken)
    {
        try
        {
            if (!rootDirectory.Exists)
            {
                return new WorkflowInstanceFindResult { Success = true, WorkflowInstances = [] };
            }

            var workflowInstances = new List<WorkflowInstance>();

            foreach (var file in rootDirectory.EnumerateFiles("*.json", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Deserialize each persisted workflow instance and apply the caller supplied predicate.
                await using var stream = file.OpenRead();
                var workflowInstance = await JsonSerializer.DeserializeAsync<WorkflowInstance>(stream, jsonOptions, cancellationToken);
                if (workflowInstance is null)
                {
                    continue;
                }

                var normalized = NormalizeWorkflowInstance(workflowInstance);
                if (predicate(normalized))
                {
                    workflowInstances.Add(normalized);
                }
            }

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

    #region Normalization helpers

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

    private static HashSet<string> NormalizeStatuses(IReadOnlyCollection<string> statuses)
    {
        return statuses
            .Where(status => !string.IsNullOrWhiteSpace(status))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static WorkflowInstance NormalizeWorkflowInstance(WorkflowInstance workflowInstance)
    {
        workflowInstance.Correlations ??= [];
        workflowInstance.CorrelationKeys ??= [];
        workflowInstance.ProcessedEvents ??= [];
        workflowInstance.AwaitingEvents ??= [];
        workflowInstance.Metadata ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return workflowInstance;
    }

    private async Task<WorkflowInstance?> ReadAsync(FileInfo file, CancellationToken cancellationToken)
    {
        await using var stream = file.OpenRead();
        var workflowInstance = await JsonSerializer.DeserializeAsync<WorkflowInstance>(stream, jsonOptions, cancellationToken);
        return workflowInstance is null ? null : NormalizeWorkflowInstance(workflowInstance);
    }

    private async Task WriteAsync(FileInfo file, WorkflowInstance workflowInstance, CancellationToken cancellationToken)
    {
        await using var stream = file.Open(FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, workflowInstance, jsonOptions, cancellationToken);
    }

    private static string CreateConcurrencyToken() => Guid.NewGuid().ToString("N");

    #endregion

    #region File helpers

    private FileInfo GetFile(string workflowInstanceId)
    {
        // Sanitize path separators to keep each workflow instance isolated to a single JSON file.
        var sanitizedWorkflowInstanceId = workflowInstanceId.Replace("/", "_", StringComparison.Ordinal).Replace("\\", "_", StringComparison.Ordinal);
        return new FileInfo(Path.Combine(rootDirectory.FullName, $"{sanitizedWorkflowInstanceId}.json"));
    }

    #endregion
}
