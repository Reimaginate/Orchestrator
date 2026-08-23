using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Reimaginate.Orchestrator.Abstractions;
using StackExchange.Redis;

namespace Reimaginate.Orchestrator.Redis;

public static class ConfigureServices
{
    public static IServiceCollection AddRedisWorkflowInstanceLocks(this IServiceCollection services, IConnectionMultiplexer connectionMultiplexer, Action<RedisWorkflowInstanceLockOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(connectionMultiplexer);

        var options = BuildOptions(configure);

        services.RemoveAll<IWorkflowInstanceLockProvider>();
        services.AddSingleton<IWorkflowInstanceLockProvider>(sp => new RedisWorkflowInstanceLockProvider(
            connectionMultiplexer,
            options,
            sp.GetService<ILogger<RedisWorkflowInstanceLockProvider>>()));
        return services;
    }

    public static IServiceCollection AddRedisWorkflowInstanceLocks(this IServiceCollection services, string connectionString, Action<RedisWorkflowInstanceLockOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = BuildOptions(configure);

        services.RemoveAll<IWorkflowInstanceLockProvider>();
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(connectionString));
        services.AddSingleton<IWorkflowInstanceLockProvider>(sp => new RedisWorkflowInstanceLockProvider(
            sp.GetRequiredService<IConnectionMultiplexer>(),
            options,
            sp.GetService<ILogger<RedisWorkflowInstanceLockProvider>>()));
        return services;
    }

    private static RedisWorkflowInstanceLockOptions BuildOptions(Action<RedisWorkflowInstanceLockOptions>? configure)
    {
        var options = new RedisWorkflowInstanceLockOptions();
        configure?.Invoke(options);
        return options;
    }
}
