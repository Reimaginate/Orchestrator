using FluentAssertions;
using Reimaginate.Orchestrator.Common.Services.WorkflowInstanceLocks;
using Xunit;

namespace Reimaginate.Orchestrator.Test.Unit.Tests.InMemory.WorkflowInstanceLocks;

public class InMemoryWorkflowInstanceLockProviderTests
{
    [Fact(DisplayName = "In-memory workflow instance lock allows only one lease per instance")]
    public async Task AllowsOnlyOneLeasePerInstance()
    {
        var provider = new InMemoryWorkflowInstanceLockProvider();

        var first = await provider.TryAcquireAsync("instance-001", CancellationToken.None);
        var second = await provider.TryAcquireAsync("instance-001", CancellationToken.None);

        first.Success.Should().BeTrue();
        first.LockAcquired.Should().BeTrue();
        first.Lease.Should().NotBeNull();
        first.Lease!.LostToken.CanBeCanceled.Should().BeFalse();
        second.Success.Should().BeTrue();
        second.LockAcquired.Should().BeFalse();

        await first.Lease.DisposeAsync();
    }

    [Fact(DisplayName = "In-memory workflow instance lock can be reacquired after disposal")]
    public async Task CanBeReacquiredAfterDisposal()
    {
        var provider = new InMemoryWorkflowInstanceLockProvider();

        var first = await provider.TryAcquireAsync("instance-002", CancellationToken.None);
        await first.Lease!.DisposeAsync();

        var second = await provider.TryAcquireAsync("instance-002", CancellationToken.None);

        second.Success.Should().BeTrue();
        second.LockAcquired.Should().BeTrue();
        second.Lease.Should().NotBeNull();

        await second.Lease!.DisposeAsync();
    }

    [Fact(DisplayName = "In-memory workflow instance locks are isolated by workflow instance id")]
    public async Task AllowsDifferentInstancesAtTheSameTime()
    {
        var provider = new InMemoryWorkflowInstanceLockProvider();

        var first = await provider.TryAcquireAsync("instance-003-a", CancellationToken.None);
        var second = await provider.TryAcquireAsync("instance-003-b", CancellationToken.None);

        first.Success.Should().BeTrue();
        first.LockAcquired.Should().BeTrue();
        second.Success.Should().BeTrue();
        second.LockAcquired.Should().BeTrue();

        await first.Lease!.DisposeAsync();
        await second.Lease!.DisposeAsync();
    }

    [Fact(DisplayName = "In-memory workflow instance lock fails one concurrent acquire for the same instance")]
    public async Task FailsOneConcurrentAcquireForSameInstance()
    {
        var provider = new InMemoryWorkflowInstanceLockProvider();

        var results = await Task.WhenAll(
            provider.TryAcquireAsync("instance-004", CancellationToken.None),
            provider.TryAcquireAsync("instance-004", CancellationToken.None));

        results.Count(result => result.Success && result.LockAcquired).Should().Be(1);
        results.Count(result => result.Success && !result.LockAcquired).Should().Be(1);

        foreach (var lease in results.Select(result => result.Lease).Where(lease => lease is not null))
        {
            await lease!.DisposeAsync();
        }
    }
}
