using System.Collections.Concurrent;
using System.Text.Json;
using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.Common.Stores.Instances;

public sealed class InMemoryWorkflowInstanceStore : IWorkflowInstanceStore
{
    #region Fields

    private readonly ConcurrentDictionary<string, WorkflowInstance> instances = new(StringComparer.OrdinalIgnoreCase);

    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    #endregion

    #region Public API

    public Task<WorkflowInstanceGetResult> GetAsync(string workflowInstanceId, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!instances.TryGetValue(workflowInstanceId, out var workflowInstance))
            {
                return Task.FromResult(new WorkflowInstanceGetResult
                {
                    Success = true,
                    NotFound = true
                });
            }

            return Task.FromResult(new WorkflowInstanceGetResult
            {
                Success = true,
                WorkflowInstance = CloneAndNormalize(workflowInstance)
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new WorkflowInstanceGetResult
            {
                Success = false,
                FailureReason = ex.Message
            });
        }
    }

    public Task<WorkflowInstanceUpsertResult> UpsertAsync(WorkflowInstance workflowInstance, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var normalizedWorkflowInstance = CloneAndNormalize(workflowInstance);
            normalizedWorkflowInstance.ConcurrencyToken = CreateConcurrencyToken();
            instances[normalizedWorkflowInstance.Id] = normalizedWorkflowInstance;

            return Task.FromResult(new WorkflowInstanceUpsertResult
            {
                Success = true,
                ConcurrencyToken = normalizedWorkflowInstance.ConcurrencyToken
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new WorkflowInstanceUpsertResult
            {
                Success = false,
                FailureReason = ex.Message
            });
        }
    }

    public Task<WorkflowInstanceWriteResult> CreateAsync(WorkflowInstance workflowInstance, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var normalizedWorkflowInstance = CloneAndNormalize(workflowInstance);
            normalizedWorkflowInstance.ConcurrencyToken = CreateConcurrencyToken();

            if (!instances.TryAdd(normalizedWorkflowInstance.Id, normalizedWorkflowInstance))
            {
                return Task.FromResult(new WorkflowInstanceWriteResult
                {
                    Success = false,
                    Conflict = true
                });
            }

            return Task.FromResult(new WorkflowInstanceWriteResult
            {
                Success = true,
                ConcurrencyToken = normalizedWorkflowInstance.ConcurrencyToken
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new WorkflowInstanceWriteResult
            {
                Success = false,
                FailureReason = ex.Message
            });
        }
    }

    public Task<WorkflowInstanceWriteResult> ReplaceAsync(WorkflowInstance workflowInstance, string expectedConcurrencyToken, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!instances.TryGetValue(workflowInstance.Id, out var existing))
            {
                return Task.FromResult(new WorkflowInstanceWriteResult
                {
                    Success = false,
                    NotFound = true
                });
            }

            if (!string.Equals(existing.ConcurrencyToken, expectedConcurrencyToken, StringComparison.Ordinal))
            {
                return Task.FromResult(new WorkflowInstanceWriteResult
                {
                    Success = false,
                    Conflict = true
                });
            }

            var normalizedWorkflowInstance = CloneAndNormalize(workflowInstance);
            normalizedWorkflowInstance.ConcurrencyToken = CreateConcurrencyToken();

            if (!instances.TryUpdate(normalizedWorkflowInstance.Id, normalizedWorkflowInstance, existing))
            {
                return Task.FromResult(new WorkflowInstanceWriteResult
                {
                    Success = false,
                    Conflict = true
                });
            }

            return Task.FromResult(new WorkflowInstanceWriteResult
            {
                Success = true,
                ConcurrencyToken = normalizedWorkflowInstance.ConcurrencyToken
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new WorkflowInstanceWriteResult
            {
                Success = false,
                FailureReason = ex.Message
            });
        }
    }

    public Task<WorkflowInstanceFindResult> FindByCorrelationAsync(IReadOnlyCollection<WorkflowResolvedCorrelationKey> correlationKeys, IReadOnlyCollection<string> statuses, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var normalizedStatuses = NormalizeStatuses(statuses);
            if (normalizedStatuses.Count == 0)
            {
                return Task.FromResult(new WorkflowInstanceFindResult { Success = true, WorkflowInstances = [] });
            }

            var normalizedKeys = correlationKeys
                .Where(x => !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.Value))
                .ToArray();

            var matches = instances.Values
                .Select(CloneAndNormalize)
                .Where(workflowInstance => normalizedStatuses.Contains(workflowInstance.Status)
                    && HasAllCorrelationKeys(workflowInstance, normalizedKeys))
                .ToList();

            return Task.FromResult(new WorkflowInstanceFindResult
            {
                Success = true,
                WorkflowInstances = matches
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new WorkflowInstanceFindResult
            {
                Success = false,
                FailureReason = ex.Message
            });
        }
    }

    public Task<WorkflowInstanceFindResult> FindByOriginatingEventIdAsync(string originatingEventId, string workflowType, IReadOnlyCollection<string> statuses, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(originatingEventId) || string.IsNullOrWhiteSpace(workflowType))
            {
                return Task.FromResult(new WorkflowInstanceFindResult { Success = true, WorkflowInstances = [] });
            }

            var normalizedStatuses = NormalizeStatuses(statuses);
            if (normalizedStatuses.Count == 0)
            {
                return Task.FromResult(new WorkflowInstanceFindResult { Success = true, WorkflowInstances = [] });
            }

            var matches = instances.Values
                .Select(CloneAndNormalize)
                .Where(workflowInstance => string.Equals(workflowInstance.OriginatingEventId, originatingEventId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(workflowInstance.WorkflowType, workflowType, StringComparison.OrdinalIgnoreCase)
                    && normalizedStatuses.Contains(workflowInstance.Status))
                .ToList();

            return Task.FromResult(new WorkflowInstanceFindResult
            {
                Success = true,
                WorkflowInstances = matches
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new WorkflowInstanceFindResult
            {
                Success = false,
                FailureReason = ex.Message
            });
        }
    }

    #endregion

    #region Helpers

    private static HashSet<string> NormalizeStatuses(IReadOnlyCollection<string> statuses)
    {
        return statuses
            .Where(status => !string.IsNullOrWhiteSpace(status))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasAllCorrelationKeys(WorkflowInstance workflowInstance, IReadOnlyCollection<WorkflowResolvedCorrelationKey> correlationKeys)
    {
        if (correlationKeys.Count == 0)
        {
            return false;
        }

        if (workflowInstance.CorrelationKeys.Count == 0)
        {
            return true;
        }

        return correlationKeys.All(key => workflowInstance.CorrelationKeys.Any(existing =>
            string.Equals(existing.Name, key.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.Value, key.Value, StringComparison.OrdinalIgnoreCase)));
    }

    private WorkflowInstance CloneAndNormalize(WorkflowInstance workflowInstance)
    {
        var clone = JsonSerializer.Deserialize<WorkflowInstance>(JsonSerializer.Serialize(workflowInstance, jsonOptions), jsonOptions)
            ?? throw new InvalidOperationException($"Workflow instance '{workflowInstance.Id}' could not be cloned.");

        clone.Correlations ??= [];
        clone.CorrelationKeys ??= [];
        clone.ProcessedEvents ??= [];
        clone.AwaitingEvents ??= [];
        clone.Metadata ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return clone;
    }

    private static string CreateConcurrencyToken() => Guid.NewGuid().ToString("N");

    #endregion
}
