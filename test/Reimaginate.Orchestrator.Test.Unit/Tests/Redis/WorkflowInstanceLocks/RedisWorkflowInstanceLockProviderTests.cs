using FluentAssertions;
using NSubstitute;
using Reimaginate.Orchestrator.Redis;
using StackExchange.Redis;
using Xunit;

namespace Reimaginate.Orchestrator.Test.Unit.Tests.Redis.WorkflowInstanceLocks;

public sealed class RedisWorkflowInstanceLockProviderTests
{
    [Fact]
    public async Task TryAcquireAsync_WhenRedisAcceptsLock_ReturnsLeaseAndUsesExpectedKeyAndTtl()
    {
        var database = Substitute.For<IDatabase>();
        database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>())
            .Returns(Task.FromResult(true));

        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(default, default).ReturnsForAnyArgs(database);

        var options = new RedisWorkflowInstanceLockOptions
        {
            KeyPrefix = "locks:",
            LeaseDuration = TimeSpan.FromSeconds(30)
        };
        var provider = new RedisWorkflowInstanceLockProvider(multiplexer, options);

        var result = await provider.TryAcquireAsync("instance-1", CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LockAcquired.Should().BeTrue();
        result.Lease.Should().NotBeNull();

        await database.Received(1).StringSetAsync(
            "locks:instance-1",
            Arg.Any<RedisValue>(),
            TimeSpan.FromSeconds(30),
            When.NotExists);
    }

    [Fact]
    public async Task TryAcquireAsync_WhenRedisRejectsLock_ReturnsConflictWithoutLease()
    {
        var database = Substitute.For<IDatabase>();
        database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>())
            .Returns(Task.FromResult(false));

        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(default, default).ReturnsForAnyArgs(database);

        var provider = new RedisWorkflowInstanceLockProvider(multiplexer);

        var result = await provider.TryAcquireAsync("instance-2", CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LockAcquired.Should().BeFalse();
        result.Lease.Should().BeNull();
    }

    [Fact]
    public async Task LeaseDisposeAsync_ReleasesLockWithCompareAndDeleteScript()
    {
        var database = Substitute.For<IDatabase>();
        database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>())
            .Returns(Task.FromResult(true));
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(RedisResult.Create(1)));

        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(default, default).ReturnsForAnyArgs(database);

        var provider = new RedisWorkflowInstanceLockProvider(multiplexer);
        var result = await provider.TryAcquireAsync("instance-3", CancellationToken.None);

        await result.Lease!.DisposeAsync();

        await database.Received(1).ScriptEvaluateAsync(
            Arg.Is<string>(script => script != null && script.Contains("redis.call('get'", StringComparison.Ordinal) && script.Contains("redis.call('del'", StringComparison.Ordinal)),
            Arg.Is<RedisKey[]>(keys => keys != null && keys.Length == 1 && keys[0] == "orchestrator:workflow-instance-lock:instance-3"),
            Arg.Is<RedisValue[]>(values => values != null && values.Length == 1 && !values[0].IsNullOrEmpty),
            CommandFlags.None);
    }

    [Fact]
    public async Task TryAcquireAsync_WhenRedisAcceptsLock_StartsRenewalWithExpectedKeyTokenAndTtl()
    {
        var renewalObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var database = Substitute.For<IDatabase>();
        database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>())
            .Returns(Task.FromResult(true));
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                var script = call.ArgAt<string>(0);
                if (script.Contains("pexpire", StringComparison.Ordinal))
                {
                    var keys = call.ArgAt<RedisKey[]>(1);
                    var values = call.ArgAt<RedisValue[]>(2);

                    keys.Should().ContainSingle().Which.Should().Be("orchestrator:workflow-instance-lock:instance-renew");
                    values.Should().HaveCount(2);
                    values[0].IsNullOrEmpty.Should().BeFalse();
                    values[1].Should().Be(90000);
                    renewalObserved.TrySetResult();
                }

                return Task.FromResult(RedisResult.Create(1));
            });

        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(default, default).ReturnsForAnyArgs(database);

        var provider = new RedisWorkflowInstanceLockProvider(multiplexer, new RedisWorkflowInstanceLockOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(90),
            RenewalInterval = TimeSpan.FromMilliseconds(10)
        });

        var result = await provider.TryAcquireAsync("instance-renew", CancellationToken.None);

        await WaitForAsync(renewalObserved.Task);
        result.Lease!.LostToken.IsCancellationRequested.Should().BeFalse();

        await result.Lease.DisposeAsync();
    }

    [Fact]
    public async Task LeaseDisposeAsync_StopsRenewal()
    {
        var renewalCount = 0;
        var database = Substitute.For<IDatabase>();
        database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>())
            .Returns(Task.FromResult(true));
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                var script = call.ArgAt<string>(0);
                if (script.Contains("pexpire", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref renewalCount);
                }

                return Task.FromResult(RedisResult.Create(1));
            });

        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(default, default).ReturnsForAnyArgs(database);

        var provider = new RedisWorkflowInstanceLockProvider(multiplexer, new RedisWorkflowInstanceLockOptions
        {
            RenewalInterval = TimeSpan.FromMilliseconds(100)
        });

        var result = await provider.TryAcquireAsync("instance-dispose", CancellationToken.None);

        var dispose = async () => await result.Lease!.DisposeAsync();

        await dispose.Should().NotThrowAsync();
        await Task.Delay(250, TestContext.Current.CancellationToken);

        renewalCount.Should().Be(0);
        result.Lease!.LostToken.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task Renewal_WhenTokenNoLongerMatches_CancelsLostToken()
    {
        var database = Substitute.For<IDatabase>();
        database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>())
            .Returns(Task.FromResult(true));
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                var script = call.ArgAt<string>(0);
                return Task.FromResult(RedisResult.Create(script.Contains("pexpire", StringComparison.Ordinal) ? 0 : 1));
            });

        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(default, default).ReturnsForAnyArgs(database);

        var provider = new RedisWorkflowInstanceLockProvider(multiplexer, new RedisWorkflowInstanceLockOptions
        {
            RenewalInterval = TimeSpan.FromMilliseconds(10)
        });

        var result = await provider.TryAcquireAsync("instance-lost", CancellationToken.None);

        await WaitForCancellationAsync(result.Lease!.LostToken);

        await result.Lease.DisposeAsync();
    }

    [Fact]
    public async Task Renewal_WhenRedisKeepsFailing_CancelsLostTokenAfterThreshold()
    {
        var database = Substitute.For<IDatabase>();
        database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>())
            .Returns(Task.FromResult(true));
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                var script = call.ArgAt<string>(0);
                return script.Contains("pexpire", StringComparison.Ordinal)
                    ? Task.FromException<RedisResult>(new TimeoutException("renewal timed out"))
                    : Task.FromResult(RedisResult.Create(1));
            });

        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(default, default).ReturnsForAnyArgs(database);

        var provider = new RedisWorkflowInstanceLockProvider(multiplexer, new RedisWorkflowInstanceLockOptions
        {
            RenewalInterval = TimeSpan.FromMilliseconds(10),
            RenewalFailureThreshold = 2
        });

        var result = await provider.TryAcquireAsync("instance-renewal-failures", CancellationToken.None);

        await WaitForCancellationAsync(result.Lease!.LostToken);

        await result.Lease.DisposeAsync();
    }

    [Fact]
    public async Task LeaseDisposeAsync_WhenReleaseFails_SuppressesByDefault()
    {
        var database = Substitute.For<IDatabase>();
        database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>())
            .Returns(Task.FromResult(true));
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                var script = call.ArgAt<string>(0);
                return script.Contains("del", StringComparison.Ordinal)
                    ? Task.FromException<RedisResult>(new TimeoutException("release timed out"))
                    : Task.FromResult(RedisResult.Create(1));
            });

        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(default, default).ReturnsForAnyArgs(database);

        var provider = new RedisWorkflowInstanceLockProvider(multiplexer, new RedisWorkflowInstanceLockOptions
        {
            RenewalInterval = TimeSpan.FromMinutes(1)
        });

        var result = await provider.TryAcquireAsync("instance-release-failure", CancellationToken.None);

        var dispose = async () => await result.Lease!.DisposeAsync();

        await dispose.Should().NotThrowAsync();
    }

    private static async Task WaitForAsync(Task task)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().Be(task);
        await task;
    }

    private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), cancellationObserved);
        await WaitForAsync(cancellationObserved.Task);
    }
}
