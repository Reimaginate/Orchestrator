using Reimaginate.Orchestrator.Common.Config;
using Reimaginate.Orchestrator.Common.Services.WorkflowExecution;

namespace Reimaginate.Orchestrator.CommandLine;

internal static class WorkflowExecutionPolicyCommandOptions
{
    public const string CheckpointsDescription = "Override workflow checkpoint persistence: Enabled, Disabled, or Failure";
    public const string WorkflowInstancesDescription = "Override workflow instance persistence: Enabled, Disabled, or Failure";

    public static bool TryCreate(
        string? checkpoints,
        string? workflowInstances,
        out WorkflowExecutionPolicyOverride? executionPolicyOverride,
        out string failureReason)
    {
        executionPolicyOverride = null;
        failureReason = string.Empty;

        if (!TryParse(checkpoints, nameof(checkpoints), out WorkflowCheckpointingMode? checkpointMode, out failureReason)
            || !TryParse(workflowInstances, "workflow-instances", out WorkflowInstancePersistenceMode? workflowInstanceMode, out failureReason))
        {
            return false;
        }

        if (checkpointMode is null && workflowInstanceMode is null)
        {
            return true;
        }

        executionPolicyOverride = new WorkflowExecutionPolicyOverride(checkpointMode, workflowInstanceMode);
        return true;
    }

    private static bool TryParse<TEnum>(string? value, string optionName, out TEnum? parsed, out string failureReason)
        where TEnum : struct, Enum
    {
        parsed = null;
        failureReason = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (Enum.TryParse<TEnum>(value.Trim(), ignoreCase: true, out var result))
        {
            parsed = result;
            return true;
        }

        failureReason = $"--{optionName} must be one of: Enabled, Disabled, Failure.";
        return false;
    }
}
