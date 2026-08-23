using Microsoft.Extensions.Options;
using Reimaginate.Orchestrator.Common.Config;

namespace Reimaginate.Orchestrator.Common.Diagnostics;

public sealed class WorkflowDiagnosticsPathResolver(IOptions<OrchestratorOptions> options) : IWorkflowDiagnosticsPathResolver
{
    private readonly OrchestratorOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public bool IsEnabled => _options.Diagnostics.Enabled;

    public string? TryGetRootPath()
    {
        if (!IsEnabled)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(_options.Diagnostics.OutputRootPath))
        {
            return _options.Diagnostics.OutputRootPath;
        }

        if (TryResolveSiblingRoot(_options.WorkflowInstanceStorage.FileSystemRootPath, out var siblingRoot)
            || TryResolveSiblingRoot(_options.WorkflowCheckpointStorage.FileSystemRootPath, out siblingRoot)
            || TryResolveSiblingRoot(_options.WorkflowDefinitionStorage.FileSystemRootPath, out siblingRoot))
        {
            return siblingRoot;
        }

        return Path.Combine(Path.GetTempPath(), "Reimaginate", "Orchestrator", "Diagnostics");
    }

    public string? TryGetWorkflowSessionPath(string workflowInstanceId)
    {
        var rootPath = TryGetRootPath();
        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(workflowInstanceId))
        {
            return null;
        }

        return Path.Combine(rootPath, SanitizeSegment(workflowInstanceId));
    }

    public string? TryGetWorkflowTypeGraphPath(string workflowType)
    {
        var rootPath = TryGetRootPath();
        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(workflowType))
        {
            return null;
        }

        return Path.Combine(rootPath, "graphs", $"{SanitizeSegment(workflowType)}.graph.json");
    }

    internal static string SanitizeSegment(string value)
    {
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalidChar, '_');
        }

        return value
            .Replace("/", "_", StringComparison.Ordinal)
            .Replace("\\", "_", StringComparison.Ordinal);
    }

    private static bool TryResolveSiblingRoot(string? existingRootPath, out string? diagnosticsRootPath)
    {
        diagnosticsRootPath = null;
        if (string.IsNullOrWhiteSpace(existingRootPath))
        {
            return false;
        }

        var parent = Directory.GetParent(existingRootPath);
        diagnosticsRootPath = parent is null
            ? Path.Combine(existingRootPath, "Diagnostics")
            : Path.Combine(parent.FullName, "Diagnostics");
        return true;
    }
}
