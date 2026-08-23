namespace Reimaginate.Orchestrator.Test.Unit.Base;

public sealed class TemporaryDirectory(string? prefix = null) : IDisposable
{
    public DirectoryInfo Directory { get; } = CreateDirectory(prefix);

    public DirectoryInfo CreateSubdirectory(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Directory.CreateSubdirectory(name);
    }

    public void Dispose()
    {
        if (Directory.Exists)
        {
            System.IO.Directory.Delete(Directory.FullName, recursive: true);
        }
    }

    private static DirectoryInfo CreateDirectory(string? prefix)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "Reimaginate.Orchestrator.Test.Unit",
            prefix ?? "temp",
            Guid.NewGuid().ToString("N"));

        return System.IO.Directory.CreateDirectory(path);
    }
}
