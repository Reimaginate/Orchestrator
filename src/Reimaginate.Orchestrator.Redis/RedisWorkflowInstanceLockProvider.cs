using Reimaginate.Orchestrator.Abstractions;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Reimaginate.Orchestrator.Redis;

public sealed class RedisWorkflowInstanceLockProvider : IWorkflowInstanceLockProvider
{
    private const string ReleaseScript = """
        if redis.call('get', KEYS[1]) == ARGV[1] then
            return redis.call('del', KEYS[1])
        else
            return 0
        end
        """;

    private const string RenewScript = """
        if redis.call('get', KEYS[1]) == ARGV[1] then
            return redis.call('pexpire', KEYS[1], ARGV[2])
        else
            return 0
        end
        """;

    private readonly IConnectionMultiplexer connectionMultiplexer;
    private readonly RedisWorkflowInstanceLockOptions options;
    private readonly ILogger<RedisWorkflowInstanceLockProvider>? logger;

    public RedisWorkflowInstanceLockProvider(
        IConnectionMultiplexer connectionMultiplexer,
        RedisWorkflowInstanceLockOptions? options = null,
        ILogger<RedisWorkflowInstanceLockProvider>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(connectionMultiplexer);

        this.connectionMultiplexer = connectionMultiplexer;
        this.options = options ?? new RedisWorkflowInstanceLockOptions();
        this.logger = logger;
    }

    public async Task<WorkflowInstanceLockAcquireResult> TryAcquireAsync(string workflowInstanceId, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(workflowInstanceId);
            ValidateOptions(options);

            var database = connectionMultiplexer.GetDatabase();
            var key = BuildKey(workflowInstanceId);
            var token = Guid.NewGuid().ToString("N");
            var acquired = await database.StringSetAsync(key, token, options.LeaseDuration, When.NotExists);

            return new WorkflowInstanceLockAcquireResult
            {
                Success = true,
                LockAcquired = acquired,
                Lease = acquired
                    ? new RedisWorkflowInstanceLockLease(
                        database,
                        workflowInstanceId,
                        key,
                        token,
                        options.LeaseDuration,
                        GetEffectiveRenewalInterval(options),
                        options.RenewalFailureThreshold,
                        options.ReleaseFailureBehavior,
                        logger)
                    : null
            };
        }
        catch (Exception ex)
        {
            return new WorkflowInstanceLockAcquireResult
            {
                Success = false,
                FailureReason = ex.Message
            };
        }
    }

    private RedisKey BuildKey(string workflowInstanceId)
    {
        return $"{options.KeyPrefix}{workflowInstanceId}";
    }

    private static void ValidateOptions(RedisWorkflowInstanceLockOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.KeyPrefix);

        if (options.LeaseDuration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Redis workflow instance lock lease duration must be greater than zero.");
        }

        if (options.RenewalInterval is { } renewalInterval && renewalInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Redis workflow instance lock renewal interval must be greater than zero.");
        }

        if (options.RenewalInterval is { } configuredRenewalInterval && configuredRenewalInterval >= options.LeaseDuration)
        {
            throw new InvalidOperationException("Redis workflow instance lock renewal interval must be shorter than the lease duration.");
        }

        if (options.RenewalFailureThreshold <= 0)
        {
            throw new InvalidOperationException("Redis workflow instance lock renewal failure threshold must be greater than zero.");
        }
    }

    private static TimeSpan GetEffectiveRenewalInterval(RedisWorkflowInstanceLockOptions options)
    {
        return options.RenewalInterval ?? TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerMillisecond, options.LeaseDuration.Ticks / 3));
    }

    private sealed class RedisWorkflowInstanceLockLease : IWorkflowInstanceLockLease
    {
        private readonly IDatabase database;
        private readonly RedisKey key;
        private readonly RedisValue token;
        private readonly TimeSpan leaseDuration;
        private readonly int renewalFailureThreshold;
        private readonly RedisWorkflowInstanceLockReleaseFailureBehavior releaseFailureBehavior;
        private readonly ILogger<RedisWorkflowInstanceLockProvider>? logger;
        private readonly PeriodicTimer renewalTimer;
        private readonly CancellationTokenSource lostCts = new();
        private int disposed;

        public RedisWorkflowInstanceLockLease(
            IDatabase database,
            string workflowInstanceId,
            RedisKey key,
            RedisValue token,
            TimeSpan leaseDuration,
            TimeSpan renewalInterval,
            int renewalFailureThreshold,
            RedisWorkflowInstanceLockReleaseFailureBehavior releaseFailureBehavior,
            ILogger<RedisWorkflowInstanceLockProvider>? logger)
        {
            this.database = database;
            this.key = key;
            this.token = token;
            this.leaseDuration = leaseDuration;
            this.renewalFailureThreshold = renewalFailureThreshold;
            this.releaseFailureBehavior = releaseFailureBehavior;
            this.logger = logger;
            renewalTimer = new PeriodicTimer(renewalInterval);
            WorkflowInstanceId = workflowInstanceId;
            _ = Task.Run(RenewUntilDisposedAsync);
        }

        public string WorkflowInstanceId { get; }
        public CancellationToken LostToken => lostCts.Token;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            renewalTimer.Dispose();

            try
            {
                await database.ScriptEvaluateAsync(ReleaseScript, [key], [token]);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to release Redis workflow instance lock for '{WorkflowInstanceId}'. The lock will expire by TTL if ownership is still held.", WorkflowInstanceId);

                if (releaseFailureBehavior == RedisWorkflowInstanceLockReleaseFailureBehavior.Throw)
                {
                    throw;
                }
            }
        }

        private async Task RenewUntilDisposedAsync()
        {
            var failureCount = 0;

            try
            {
                while (await renewalTimer.WaitForNextTickAsync())
                {
                    if (disposed != 0)
                    {
                        return;
                    }

                    try
                    {
                        var renewed = await TryRenewAsync();
                        if (!renewed)
                        {
                            if (disposed != 0)
                            {
                                return;
                            }

                            logger?.LogError("Redis workflow instance lock for '{WorkflowInstanceId}' was lost before renewal.", WorkflowInstanceId);
                            lostCts.Cancel();
                            return;
                        }

                        failureCount = 0;
                    }
                    catch (Exception ex)
                    {
                        if (disposed != 0)
                        {
                            return;
                        }

                        failureCount++;
                        logger?.LogWarning(ex, "Failed to renew Redis workflow instance lock for '{WorkflowInstanceId}' ({FailureCount}/{FailureThreshold}).", WorkflowInstanceId, failureCount, renewalFailureThreshold);

                        if (failureCount >= renewalFailureThreshold)
                        {
                            logger?.LogError("Redis workflow instance lock for '{WorkflowInstanceId}' was lost after {FailureThreshold} consecutive renewal failures.", WorkflowInstanceId, renewalFailureThreshold);
                            lostCts.Cancel();
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (disposed != 0)
                {
                    return;
                }

                logger?.LogError(ex, "Redis workflow instance lock renewal loop failed for '{WorkflowInstanceId}'.", WorkflowInstanceId);
                lostCts.Cancel();
            }
        }

        private async Task<bool> TryRenewAsync()
        {
            var leaseMilliseconds = (long)Math.Ceiling(leaseDuration.TotalMilliseconds);
            var result = await database.ScriptEvaluateAsync(RenewScript, [key], [token, leaseMilliseconds]);
            return (long)result == 1;
        }
    }
}
