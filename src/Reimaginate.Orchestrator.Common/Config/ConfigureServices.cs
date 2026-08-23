using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Reimaginate.CLI.Base.Config;
using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.Common.Diagnostics;
using Reimaginate.Orchestrator.Common.Helpers;
using Reimaginate.Orchestrator.Common.Services.WorkflowCorrelation;
using Reimaginate.Orchestrator.Common.Services.WorkflowDefinitions;
using Reimaginate.Orchestrator.Common.Services.WorkflowEventBindings;
using Reimaginate.Orchestrator.Common.Services.WorkflowEventDispatch;
using Reimaginate.Orchestrator.Common.Services.WorkflowEventEmitters;
using Reimaginate.Orchestrator.Common.Services.WorkflowEventNormalization;
using Reimaginate.Orchestrator.Common.Services.WorkflowEventProcessing;
using Reimaginate.Orchestrator.Common.Services.WorkflowEventReceivers;
using Reimaginate.Orchestrator.Common.Services.WorkflowExecution;
using Reimaginate.Orchestrator.Common.Services.WorkflowInstancePersistence;
using Reimaginate.Orchestrator.Common.Services.WorkflowInstanceMutation;
using Reimaginate.Orchestrator.Common.Services.WorkflowInstanceLocks;
using Reimaginate.Orchestrator.Common.Services.WorkflowResume;
using Reimaginate.Orchestrator.Common.Services.Shutdown;
using Reimaginate.Orchestrator.Common.Stores.Checkpoints;
using Reimaginate.Orchestrator.Common.Stores.Instances;
using Reimaginate.Orchestrator.Common.Stores.WorkflowDefinitions;

namespace Reimaginate.Orchestrator.Common.Config;

public static class ConfigureServices
{
    public static IServiceCollection AddOrchestratorServices(this IServiceCollection services, IConfiguration config, Type? workflowActionResolver = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        services.AddBaseCommands(config);
        services.TryAddSingleton(config);

        var orchestratorOptionsSection = ResolveOrchestratorOptionsSection(config);

        services.Configure<OrchestratorOptions>(orchestratorOptionsSection);
        services.Configure<WorkflowBuilderAdapterOptions>(config.GetSection(WorkflowBuilderAdapterOptions.SectionName));
        AddDefaultInfrastructure(services);
        AddConfiguredStorage(services);
        AddDiagnostics(services);

        services.AddScoped<Channel<JsonObject>>(_ => Channel.CreateBounded<JsonObject>(new BoundedChannelOptions(100)));

        services.AddTransient<IWorkflowBuilderAdapterFactory, WorkflowBuilderAdapterFactory>();
        services.AddTransient<IWorkflowCorrelationService, WorkflowCorrelationService>();
        services.AddTransient<IWorkflowDefinitionService, WorkflowDefinitionService>();
        services.AddTransient<IWorkflowEventBindingService, WorkflowEventBindingService>();
        services.AddTransient<IWorkflowEventDeduplicationKeyStrategy, WorkflowEventDeduplicationKeyStrategy>();
        services.AddTransient<IWorkflowEventDispatchService, WorkflowEventDispatchService>();
        services.AddTransient<IWorkflowEventMessageNormalizationService, WorkflowEventMessageNormalizationService>();
        services.AddTransient<IWorkflowEventNormalizationService, WorkflowEventNormalizationService>();
        services.AddTransient<IWorkflowEventProcessingService, WorkflowEventProcessingService>();
        services.AddTransient<IWorkflowEventReceiverService, WorkflowEventReceiverService>();
        services.TryAddSingleton<IWorkflowExecutionPolicyProvider, ConfiguredWorkflowExecutionPolicyProvider>();
        services.AddTransient<IWorkflowInstanceMutationService, WorkflowInstanceMutationService>();
        services.AddTransient<IWorkflowInstancePersistenceService, WorkflowInstancePersistenceService>();
        services.AddTransient<IWorkflowResumeRuntimeService, WorkflowResumeRuntimeService>();
        services.TryAddSingleton<IWorkflowEnvironmentProvider, ConfiguredWorkflowEnvironmentProvider>();
        services.TryAddSingleton<IOrchestratorShutdownSignal, HostOrchestratorShutdownSignal>();

        if (workflowActionResolver != null)
        {
            services.AddSingleton(typeof(IWorkflowActionResolver), workflowActionResolver);
        }
        else
        {
            services.AddSingleton<IWorkflowActionResolver, WorkflowActionResolver>();
        }

        return services;
    }

    public static IServiceCollection AddFileSystemCheckpointStorage(this IServiceCollection services, DirectoryInfo directory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(directory);

        services.RemoveAll<ICheckpointStorage>();
        services.AddSingleton<ICheckpointStorage>(_ => new FileSystemCheckpointStorage(directory));
        return services;
    }

    public static IServiceCollection AddFileSystemWorkflowDefinitionStore(this IServiceCollection services, DirectoryInfo directory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(directory);

        services.RemoveAll<IWorkflowDefinitionStore>();
        services.AddSingleton<IWorkflowDefinitionStore>(_ => new FileSystemWorkflowDefinitionStore(directory));
        return services;
    }

    public static IServiceCollection AddFileSystemWorkflowInstanceStore(this IServiceCollection services, DirectoryInfo directory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(directory);

        services.RemoveAll<IWorkflowInstanceStore>();
        services.AddSingleton<IWorkflowInstanceStore>(_ => new FileSystemWorkflowInstanceStore(directory));
        return services;
    }

    private static void AddDefaultInfrastructure(IServiceCollection services)
    {
        if (services.All(descriptor => descriptor.ServiceType != typeof(ICheckpointStorage)))
        {
            services.AddSingleton<InMemoryCheckpointStorage>();
            services.AddSingleton<ICheckpointStorage>(sp => sp.GetRequiredService<InMemoryCheckpointStorage>());
        }

        if (services.All(descriptor => descriptor.ServiceType != typeof(IWorkflowDefinitionStore)))
        {
            services.AddSingleton<InMemoryWorkflowDefinitionStore>();
            services.AddSingleton<IWorkflowDefinitionStore>(sp => sp.GetRequiredService<InMemoryWorkflowDefinitionStore>());
        }

        if (services.All(descriptor => descriptor.ServiceType != typeof(IWorkflowInstanceStore)))
        {
            services.AddSingleton<InMemoryWorkflowInstanceStore>();
            services.AddSingleton<IWorkflowInstanceStore>(sp => sp.GetRequiredService<InMemoryWorkflowInstanceStore>());
        }

        if (services.All(descriptor => descriptor.ServiceType != typeof(IWorkflowInstanceLockProvider)))
        {
            services.AddSingleton<InMemoryWorkflowInstanceLockProvider>();
            services.AddSingleton<IWorkflowInstanceLockProvider>(sp => sp.GetRequiredService<InMemoryWorkflowInstanceLockProvider>());
        }

        if (services.All(descriptor => descriptor.ServiceType != typeof(IWorkflowEventReceiver)))
        {
            services.AddSingleton<InMemoryChannelOrchestratorEventReceiverClient>();
            services.AddSingleton<IWorkflowEventReceiver, InMemoryChannelWorkflowEventReceiver>();
        }

        if (services.All(descriptor => descriptor.ServiceType != typeof(IWorkflowEventEmitter)))
        {
            services.AddSingleton<IWorkflowEventEmitter>(_ => new DisabledWorkflowEventEmitter("No workflow event emitter has been configured."));
        }
    }

    private static IConfiguration ResolveOrchestratorOptionsSection(IConfiguration config)
    {
        var nestedSection = config.GetSection(OrchestratorOptions.DefaultSectionName);
        return SectionExists(nestedSection) ? nestedSection : config;
    }

    private static bool SectionExists(IConfiguration section)
    {
        return (section as IConfigurationSection)?.Value is not null || section.GetChildren().Any();
    }

    private static void AddConfiguredStorage(IServiceCollection services)
    {
        services.RemoveAll<ICheckpointStorage>();
        services.AddSingleton<ICheckpointStorage>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<OrchestratorOptions>>().Value;
            if (options.WorkflowCheckpointStorage.TryResolveFileSystemRootPath(out var folderPath))
            {
                return new FileSystemCheckpointStorage(new DirectoryInfo(folderPath!));
            }

            return sp.GetRequiredService<InMemoryCheckpointStorage>();
        });

        services.RemoveAll<IWorkflowInstanceStore>();
        services.AddSingleton<IWorkflowInstanceStore>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<OrchestratorOptions>>().Value;
            if (options.WorkflowInstanceStorage.TryResolveFileSystemRootPath(out var folderPath))
            {
                return new FileSystemWorkflowInstanceStore(new DirectoryInfo(folderPath!));
            }

            return sp.GetRequiredService<InMemoryWorkflowInstanceStore>();
        });

        services.RemoveAll<IWorkflowDefinitionStore>();
        services.AddSingleton<IWorkflowDefinitionStore>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<OrchestratorOptions>>().Value;
            if (options.WorkflowDefinitionStorage.TryResolveFileSystemRootPath(out var folderPath, WorkflowDefinitionStorageDefaults.DefaultFileSystemFolderName))
            {
                return new FileSystemWorkflowDefinitionStore(new DirectoryInfo(folderPath!));
            }

            return sp.GetRequiredService<InMemoryWorkflowDefinitionStore>();
        });
    }

    private static void AddDiagnostics(IServiceCollection services)
    {
        services.TryAddSingleton<IWorkflowDiagnosticsPathResolver, WorkflowDiagnosticsPathResolver>();
        services.TryAddSingleton<IWorkflowExecutionTraceSink>(sp =>
        {
            var pathResolver = sp.GetRequiredService<IWorkflowDiagnosticsPathResolver>();
            return pathResolver.IsEnabled
                ? new FileSystemWorkflowExecutionTraceSink(pathResolver)
                : new NullWorkflowExecutionTraceSink();
        });
    }
}
