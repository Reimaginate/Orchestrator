using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Reimaginate.Orchestrator.Common.Config;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowExecution;

public sealed class ConfiguredWorkflowExecutionPolicyProvider(
    IConfiguration configuration,
    IOptions<OrchestratorOptions> options) : IWorkflowExecutionPolicyProvider
{
    public WorkflowExecutionPolicy Resolve(string? workflowType)
    {
        var checkpoints = WorkflowCheckpointingMode.Enabled;
        var workflowInstances = WorkflowInstancePersistenceMode.Enabled;

        foreach (var workflowOptions in options.Value.Environment.Workflows.Where(scope => MatchesWorkflowType(workflowType, scope.WorkflowType)))
        {
            checkpoints = workflowOptions.Execution.Checkpoints;
            workflowInstances = workflowOptions.Execution.WorkflowInstances;
        }

        foreach (var workflowSection in ResolveEnvironmentWorkflowsSection()?.GetChildren().Where(section => MatchesWorkflowType(workflowType, GetWorkflowType(section))) ?? [])
        {
            if (TryParseCheckpointingMode(workflowSection["Execution:Checkpoints"], out var configuredMode))
            {
                checkpoints = configuredMode;
            }

            if (TryParseWorkflowInstancePersistenceMode(workflowSection["Execution:WorkflowInstances"], out var configuredWorkflowInstances))
            {
                workflowInstances = configuredWorkflowInstances;
            }
        }

        return new WorkflowExecutionPolicy(checkpoints, workflowInstances);
    }

    private IConfigurationSection? ResolveEnvironmentWorkflowsSection()
    {
        var nestedSection = configuration.GetSection($"{OrchestratorOptions.DefaultSectionName}:Environment:Workflows");
        if (SectionExists(nestedSection))
        {
            return nestedSection;
        }

        var directSection = configuration.GetSection("Environment:Workflows");
        return SectionExists(directSection) ? directSection : null;
    }

    private static string? GetWorkflowType(IConfigurationSection workflowSection)
        => NormalizeKey(workflowSection["WorkflowType"])
           ?? (IsNumericKey(workflowSection.Key)
               ? null
               : NormalizeKey(workflowSection.Key));

    private static bool TryParseCheckpointingMode(string? value, out WorkflowCheckpointingMode mode)
    {
        mode = WorkflowCheckpointingMode.Enabled;
        return !string.IsNullOrWhiteSpace(value)
               && Enum.TryParse(value.Trim(), ignoreCase: true, out mode);
    }

    private static bool TryParseWorkflowInstancePersistenceMode(string? value, out WorkflowInstancePersistenceMode mode)
    {
        mode = WorkflowInstancePersistenceMode.Enabled;
        return !string.IsNullOrWhiteSpace(value)
               && Enum.TryParse(value.Trim(), ignoreCase: true, out mode);
    }

    private static bool SectionExists(IConfigurationSection section)
        => section.Value is not null || section.GetChildren().Any();

    private static bool IsNumericKey(string key)
        => int.TryParse(key, out _);

    private static string? NormalizeKey(string? key)
    {
        var normalized = key?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static bool MatchesWorkflowType(string? requestedWorkflowType, string? configuredWorkflowType)
    {
        var requested = NormalizeWorkflowType(requestedWorkflowType);
        var configured = NormalizeWorkflowType(configuredWorkflowType);
        return requested is not null
               && configured is not null
               && string.Equals(requested, configured, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeWorkflowType(string? workflowType)
    {
        var normalized = workflowType?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized.Replace('\\', '/');
    }
}
