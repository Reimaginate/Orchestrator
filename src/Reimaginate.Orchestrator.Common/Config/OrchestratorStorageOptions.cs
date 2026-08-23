namespace Reimaginate.Orchestrator.Common.Config;

public sealed class OrchestratorStorageOptions
{
    public string? Provider { get; set; }
    public string? FileSystemRootPath { get; set; }

    public bool TryResolveFileSystemRootPath(out string? folderPath, string? fallbackFolder = null)
    {
        folderPath = null;

        if (!string.IsNullOrWhiteSpace(FileSystemRootPath))
        {
            folderPath = FileSystemRootPath;
            return true;
        }

        if (string.IsNullOrWhiteSpace(fallbackFolder))
        {
            return false;
        }

        var fallbackPath = Path.Combine(AppContext.BaseDirectory, fallbackFolder);
        if (!Directory.Exists(fallbackPath))
        {
            throw new DirectoryNotFoundException($"The specified fallback path '{fallbackPath}' does not exist.");
        }

        folderPath = fallbackPath;
        return true;
    }
}