using Microsoft.Extensions.Options;
using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.Common.Config;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowInstanceMutation;

public sealed class WorkflowInstanceMutationService(
    IWorkflowInstanceStore workflowInstanceStore,
    IWorkflowInstanceLockProvider workflowInstanceLockProvider,
    IOptions<OrchestratorOptions>? orchestratorOptions = null) : IWorkflowInstanceMutationService
{
    private readonly OrchestratorWorkflowInstanceMutationOptions mutationOptions = NormalizeOptions(orchestratorOptions?.Value.WorkflowInstanceMutation);

    public async Task<WorkflowInstanceMutationResult> CreateAsync(WorkflowInstance workflowInstance, CancellationToken cancellationToken, string? lockKey = null)
    {
        await using var lease = await AcquireLockAsync(lockKey, cancellationToken);

        var createResult = await workflowInstanceStore.CreateAsync(workflowInstance, cancellationToken);
        if (!createResult.Success)
        {
            return new WorkflowInstanceMutationResult
            {
                Success = false,
                Conflict = createResult.Conflict,
                NotFound = createResult.NotFound,
                FailureReason = createResult.FailureReason
            };
        }

        workflowInstance.ConcurrencyToken = createResult.ConcurrencyToken;
        return new WorkflowInstanceMutationResult
        {
            Success = true,
            WorkflowInstance = workflowInstance
        };
    }

    public async Task<WorkflowInstanceMutationResult> UpsertAsync(
        string workflowInstanceId,
        Func<WorkflowInstance?, DateTimeOffset, ValueTask<WorkflowInstanceMutationCommand>> mutator,
        CancellationToken cancellationToken,
        string? lockKey = null)
    {
        await using var lease = await AcquireLockAsync(lockKey ?? workflowInstanceId, cancellationToken);

        for (var attempt = 0; attempt < mutationOptions.MaxMutationAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var getResult = await workflowInstanceStore.GetAsync(workflowInstanceId, cancellationToken);
            if (!getResult.Success)
            {
                return new WorkflowInstanceMutationResult
                {
                    Success = false,
                    FailureReason = getResult.FailureReason
                };
            }

            var current = getResult.NotFound ? null : getResult.WorkflowInstance;
            var mutation = await mutator(current, DateTimeOffset.UtcNow);
            if (mutation.NoChange)
            {
                return new WorkflowInstanceMutationResult
                {
                    Success = true,
                    WorkflowInstance = current
                };
            }

            var next = mutation.WorkflowInstance ?? throw new InvalidOperationException("Mutation command must include a workflow instance when NoChange is false.");
            next.Id = workflowInstanceId;

            WorkflowInstanceWriteResult writeResult;
            if (current is null)
            {
                writeResult = await workflowInstanceStore.CreateAsync(next, cancellationToken);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(current.ConcurrencyToken))
                {
                    return new WorkflowInstanceMutationResult
                    {
                        Success = false,
                        FailureReason = $"Workflow instance '{workflowInstanceId}' is missing a concurrency token."
                    };
                }

                writeResult = await workflowInstanceStore.ReplaceAsync(next, current.ConcurrencyToken, cancellationToken);
            }

            if (writeResult.Success)
            {
                next.ConcurrencyToken = writeResult.ConcurrencyToken;
                return new WorkflowInstanceMutationResult
                {
                    Success = true,
                    WorkflowInstance = next
                };
            }

            if (writeResult.Conflict || writeResult.NotFound)
            {
                continue;
            }

            return new WorkflowInstanceMutationResult
            {
                Success = false,
                Conflict = writeResult.Conflict,
                NotFound = writeResult.NotFound,
                FailureReason = writeResult.FailureReason
            };
        }

        return new WorkflowInstanceMutationResult
        {
            Success = false,
            Conflict = true,
            FailureReason = $"Workflow instance '{workflowInstanceId}' could not be updated after {mutationOptions.MaxMutationAttempts} attempts."
        };
    }

    private async Task<IWorkflowInstanceLockLease?> AcquireLockAsync(string? lockKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(lockKey))
        {
            return null;
        }

        var normalizedLockKey = $"workflow-instance-mutation::{lockKey}";
        for (var attempt = 0; attempt < mutationOptions.MaxLockAttempts; attempt++)
        {
            var lockAcquireResult = await workflowInstanceLockProvider.TryAcquireAsync(normalizedLockKey, cancellationToken);
            if (!lockAcquireResult.Success)
            {
                throw new InvalidOperationException(lockAcquireResult.FailureReason ?? $"Failed to acquire workflow instance mutation lock '{normalizedLockKey}'.");
            }

            if (lockAcquireResult.LockAcquired)
            {
                return lockAcquireResult.Lease
                    ?? throw new InvalidOperationException($"Workflow instance lock provider acquired '{normalizedLockKey}' without returning a lease.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(mutationOptions.LockRetryDelayMilliseconds), cancellationToken);
        }

        throw new InvalidOperationException($"Workflow instance mutation lock '{normalizedLockKey}' is unavailable.");
    }

    private static OrchestratorWorkflowInstanceMutationOptions NormalizeOptions(OrchestratorWorkflowInstanceMutationOptions? options)
    {
        options ??= new OrchestratorWorkflowInstanceMutationOptions();

        return new OrchestratorWorkflowInstanceMutationOptions
        {
            MaxMutationAttempts = Math.Max(1, options.MaxMutationAttempts),
            MaxLockAttempts = Math.Max(1, options.MaxLockAttempts),
            LockRetryDelayMilliseconds = Math.Max(1, options.LockRetryDelayMilliseconds)
        };
    }
}
