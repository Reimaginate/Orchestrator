using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.Common.Stores.WorkflowDefinitions.Parsing;

namespace Reimaginate.Orchestrator.AzureStorage;

public sealed class AzureBlobStorageWorkflowDefinitionStore(BlobContainerClient containerClient, string? blobPrefix = null) : IWorkflowDefinitionStore
{
    private const string WorkflowDefinitionFileSuffix = ".workflow.yaml";

    private readonly BlobContainerClient _containerClient = containerClient ?? throw new ArgumentNullException(nameof(containerClient));
    private readonly string _blobPrefix = (blobPrefix ?? string.Empty).Trim('/');

    public bool SupportsEventTriggerBindingQuery => true;

    public async Task<Result<string>> OpenReadAsync(string workflowType, CancellationToken cancellationToken)
    {
        var blobClient = _containerClient.GetBlobClient(GetBlobName(workflowType));

        try
        {
            var response = await blobClient.DownloadContentAsync(cancellationToken: cancellationToken);
            return new Result<string>
            {
                Success = true,
                Data = response.Value.Content.ToString()
            };
        }
        catch (Exception)
        {
            return new Result<string>
            {
                Success = false,
                FailureReason = $"Workflow definition '{workflowType}' was not found in blob container '{_containerClient.Name}'.",
            };
        }
    }

    public async Task<Result<bool>> ExistsAsync(string workflowType, CancellationToken cancellationToken)
    {
        var blobClient = _containerClient.GetBlobClient(GetBlobName(workflowType));
        try
        {
            var response = await blobClient.ExistsAsync(cancellationToken);
            return new Result<bool>()
            {
                Success = true,
                Data = response.Value
            };
        }
        catch (Exception ex)
        {
            return new Result<bool>()
            {
                Success = false,
                FailureReason = ex.Message,
            };
        }
    }

    public async IAsyncEnumerable<string> ListAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var blob in _containerClient.GetBlobsAsync(BlobTraits.None, BlobStates.None, GetWorkflowDefinitionsPrefix(), cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!blob.Name.EndsWith(WorkflowDefinitionFileSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var workflowType = TryParseWorkflowTypeFromBlobName(blob.Name);
            if (!string.IsNullOrWhiteSpace(workflowType))
            {
                yield return workflowType;
            }
        }
    }

    public async Task<IReadOnlyList<WorkflowEventTriggerBindingDescriptor>> FindBindingsByEventTypesAsync(
        IReadOnlyCollection<string> eventTypes,
        CancellationToken cancellationToken)
    {
        if (eventTypes.Count == 0)
        {
            return [];
        }

        var requestedEventTypes = new HashSet<string>(eventTypes.Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
        if (requestedEventTypes.Count == 0)
        {
            return [];
        }

        var matches = new List<WorkflowEventTriggerBindingDescriptor>();

        await foreach (var blob in _containerClient.GetBlobsAsync(BlobTraits.None, BlobStates.None, GetWorkflowDefinitionsPrefix(), cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!blob.Name.EndsWith(WorkflowDefinitionFileSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var workflowType = TryParseWorkflowTypeFromBlobName(blob.Name);
            if (string.IsNullOrWhiteSpace(workflowType))
            {
                continue;
            }

            var blobClient = _containerClient.GetBlobClient(blob.Name);
            var response = await blobClient.DownloadStreamingAsync(cancellationToken: cancellationToken);
            await using var stream = response.Value.Content;

            var bindings = WorkflowEventTriggerBindingDescriptorParser.Parse(stream, workflowType);
            matches.AddRange(bindings.Where(x => requestedEventTypes.Contains(x.EventType)));
        }

        return matches;
    }

    private string GetBlobName(string workflowType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowType);

        var normalizedWorkflowType = workflowType.EndsWith(WorkflowDefinitionFileSuffix, StringComparison.OrdinalIgnoreCase)
            ? workflowType[..^WorkflowDefinitionFileSuffix.Length]
            : workflowType;

        var sanitizedWorkflowType = SanitizeSegment(normalizedWorkflowType);
        return $"{GetWorkflowDefinitionsPrefix()}{sanitizedWorkflowType}{WorkflowDefinitionFileSuffix}";
    }

    private string GetWorkflowDefinitionsPrefix()
    {
        var root = string.IsNullOrWhiteSpace(_blobPrefix) ? "workflow-definitions" : $"{_blobPrefix}/workflow-definitions";
        return $"{root}/";
    }

    private static string SanitizeSegment(string value) => Uri.EscapeDataString(value);

    private string? TryParseWorkflowTypeFromBlobName(string blobName)
    {
        var prefix = GetWorkflowDefinitionsPrefix();
        if (!blobName.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var fileName = blobName[prefix.Length..];
        if (!fileName.EndsWith(WorkflowDefinitionFileSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var encodedWorkflowType = fileName[..^WorkflowDefinitionFileSuffix.Length];
        if (string.IsNullOrWhiteSpace(encodedWorkflowType))
        {
            return null;
        }

        var workflowType = Uri.UnescapeDataString(encodedWorkflowType);
        return string.IsNullOrWhiteSpace(workflowType)
            ? null
            : string.Concat(workflowType, WorkflowDefinitionFileSuffix);
    }
}
