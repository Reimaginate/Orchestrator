using Microsoft.Extensions.Configuration;

namespace Reimaginate.Orchestrator.Common.Helpers;

public static class ConfigHelpers
{
    public static bool ResolveOrchestratorFolderPath(this IConfiguration configuration, string? key, out string? folderPath, string? fallbackFolder = null)
    {
        folderPath = null;

        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var configuredRoot = configuration[key];
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            folderPath = configuredRoot;
            return true;
        }

        if (string.IsNullOrWhiteSpace(fallbackFolder))
        {
            return false;
        }

        var fallbackPath = Path.Combine(AppContext.BaseDirectory, fallbackFolder);
        if (!Directory.Exists(fallbackPath))
        {
            throw new Exception($"The specified fallback path {fallbackPath} does not exist");
        }

        folderPath = fallbackPath;
        return true;
    }
}