using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.Common.Stores.WorkflowDefinitions.Parsing;

namespace Reimaginate.Orchestrator.Common.Stores.WorkflowDefinitions;

public sealed class FileSystemWorkflowDefinitionStore(DirectoryInfo rootDirectory) : IWorkflowDefinitionStore
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly DirectoryInfo rootDirectory = rootDirectory ?? throw new ArgumentNullException(nameof(rootDirectory));

    public bool SupportsEventTriggerBindingQuery => true;

    public async Task<Result<string>> OpenReadAsync(string workflowType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var file = GetFile(workflowType);
            if (!file.Exists)
            {
                return new Result<string>
                {
                    Success = false,
                    FailureReason = $"Workflow definition '{workflowType}' was not found in '{rootDirectory.FullName}'."
                };
            }

            var definition = await File.ReadAllTextAsync(file.FullName, cancellationToken);
            return new Result<string>
            {
                Success = true,
                Data = definition
            };
        }
        catch (Exception ex)
        {
            return new Result<string>
            {
                Success = false,
                FailureReason = ex.Message
            };
        }
    }

    public Task<Result<bool>> ExistsAsync(string workflowType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var exists = GetFile(workflowType).Exists;
            return Task.FromResult(new Result<bool>
            {
                Success = true,
                Data = exists,
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new Result<bool>
            {
                Success = false,
                FailureReason = ex.Message
            });
        }
    }

    public async IAsyncEnumerable<string> ListAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!rootDirectory.Exists)
        {
            yield break;
        }

        foreach (var file in EnumerateWorkflowFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return ToCanonicalWorkflowType(rootDirectory.FullName, file.FullName);
            await Task.Yield();
        }
    }

    public Task<IReadOnlyList<WorkflowEventTriggerBindingDescriptor>> FindBindingsByEventTypesAsync(
        IReadOnlyCollection<string> eventTypes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!rootDirectory.Exists || eventTypes.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<WorkflowEventTriggerBindingDescriptor>>([]);
        }

        var requestedEventTypes = new HashSet<string>(eventTypes.Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
        if (requestedEventTypes.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<WorkflowEventTriggerBindingDescriptor>>([]);
        }

        var matches = new List<WorkflowEventTriggerBindingDescriptor>();

        foreach (var file in EnumerateWorkflowFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            var workflowType = ToCanonicalWorkflowType(rootDirectory.FullName, file.FullName);
            var bindings = WorkflowEventTriggerBindingDescriptorParser.Parse(stream, workflowType);
            matches.AddRange(bindings.Where(x => requestedEventTypes.Contains(x.EventType)));
        }

        return Task.FromResult<IReadOnlyList<WorkflowEventTriggerBindingDescriptor>>(matches);
    }

    private FileInfo GetFile(string workflowType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowType);

        var normalizedWorkflowType = NormalizeWorkflowTypePath(workflowType);
        var candidatePath = Path.GetFullPath(Path.Combine(rootDirectory.FullName, normalizedWorkflowType));
        var rootPath = EnsureTrailingDirectorySeparator(Path.GetFullPath(rootDirectory.FullName));

        if (!candidatePath.StartsWith(rootPath, PathComparison))
        {
            throw new ArgumentException($"Workflow definition '{workflowType}' resolves outside of the configured root directory.", nameof(workflowType));
        }

        return new FileInfo(candidatePath);
    }

    private IEnumerable<FileInfo> EnumerateWorkflowFiles()
    {
        return rootDirectory
            .EnumerateFiles("*.workflow.yaml", SearchOption.AllDirectories)
            .OrderBy(file => ToCanonicalWorkflowType(rootDirectory.FullName, file.FullName), StringComparer.OrdinalIgnoreCase);
    }

    internal static string NormalizeWorkflowTypePath(string workflowType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowType);

        var normalizedPath = workflowType
            .Trim()
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        return normalizedPath.EndsWith(".workflow.yaml", StringComparison.OrdinalIgnoreCase)
            ? normalizedPath
            : string.Concat(normalizedPath, ".workflow.yaml");
    }

    internal static string ToCanonicalWorkflowType(string rootDirectoryPath, string workflowPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowPath);

        var rootPath = EnsureTrailingDirectorySeparator(Path.GetFullPath(rootDirectoryPath));
        var candidatePath = Path.GetFullPath(workflowPath);

        if (!candidatePath.StartsWith(rootPath, PathComparison))
        {
            throw new ArgumentException($"Workflow path '{workflowPath}' resolves outside of the configured root directory.", nameof(workflowPath));
        }

        var relativePath = Path.GetRelativePath(rootPath, candidatePath);
        return relativePath
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static string EnsureTrailingDirectorySeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}
