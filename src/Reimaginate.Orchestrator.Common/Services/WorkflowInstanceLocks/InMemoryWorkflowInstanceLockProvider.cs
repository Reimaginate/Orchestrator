using System.Collections.Concurrent;
using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowInstanceLocks;

public sealed class InMemoryWorkflowInstanceLockProvider : IWorkflowInstanceLockProvider
{
    private readonly ConcurrentDictionary<string, InMemoryWorkflowInstanceLockLease> leases = new(StringComparer.OrdinalIgnoreCase);

    public Task<WorkflowInstanceLockAcquireResult> TryAcquireAsync(string workflowInstanceId, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(workflowInstanceId);

            var lease = new InMemoryWorkflowInstanceLockLease(workflowInstanceId, Release);
            if (!leases.TryAdd(workflowInstanceId, lease))
            {
                return Task.FromResult(new WorkflowInstanceLockAcquireResult
                {
                    Success = true,
                    LockAcquired = false
                });
            }

            return Task.FromResult(new WorkflowInstanceLockAcquireResult
            {
                Success = true,
                LockAcquired = true,
                Lease = lease
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new WorkflowInstanceLockAcquireResult
            {
                Success = false,
                FailureReason = ex.Message
            });
        }
    }

    private void Release(InMemoryWorkflowInstanceLockLease lease)
    {
        if (leases.TryGetValue(lease.WorkflowInstanceId, out var existing) && ReferenceEquals(existing, lease))
        {
            leases.TryRemove(lease.WorkflowInstanceId, out _);
        }
    }

    private sealed class InMemoryWorkflowInstanceLockLease(string workflowInstanceId, Action<InMemoryWorkflowInstanceLockLease> release) : IWorkflowInstanceLockLease
    {
        private int disposed;

        public string WorkflowInstanceId { get; } = workflowInstanceId;
        public CancellationToken LostToken => CancellationToken.None;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                release(this);
            }

            return ValueTask.CompletedTask;
        }
    }
}
