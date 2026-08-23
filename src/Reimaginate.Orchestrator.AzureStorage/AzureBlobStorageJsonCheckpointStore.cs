using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Reimaginate.Orchestrator.Common.Stores.Checkpoints;

namespace Reimaginate.Orchestrator.AzureStorage;

public sealed class AzureBlobStorageJsonCheckpointStore(BlobContainerClient containerClient, string? blobPrefix = null) : JsonCheckpointStore, IExplicitCheckpointStore
{
    #region Constants and fields

    // Persist a lightweight schema marker so payload shape can evolve safely in future revisions.
    private const int SchemaVersion = 1;
    private const string CheckpointFileSuffix = ".checkpoint.json";

    private readonly BlobContainerClient _containerClient = containerClient ?? throw new ArgumentNullException(nameof(containerClient));
    private readonly string _blobPrefix = (blobPrefix ?? string.Empty).Trim('/');

    #endregion

    #region JsonCheckpointStore implementation

    public override async ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string runId, CheckpointInfo? withParent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var checkpoints = new List<CheckpointInfo>();
        var prefix = GetCheckpointPrefix(runId);

        // Enumerate checkpoint blobs for the run and optionally filter by parent checkpoint metadata.
        await foreach (var blobItem in _containerClient.GetBlobsAsync(new GetBlobsOptions() { Prefix = prefix, Traits = BlobTraits.None }))
        {
            var checkpoint = TryParseCheckpointInfoFromBlobName(blobItem.Name, runId);
            if (checkpoint is null)
            {
                continue;
            }

            if (withParent is null)
            {
                checkpoints.Add(checkpoint);
                continue;
            }

            var blob = _containerClient.GetBlobClient(blobItem.Name);
            var downloaded = await blob.DownloadContentAsync();

            // Parent filtering is metadata-based to avoid materializing payloads unnecessarily.
            var checkpointEnvelope = TryDeserializeCheckpointEnvelope(downloaded.Value.Content.ToString());
            if (checkpointEnvelope is null || !ParentMatches(withParent, checkpointEnvelope.Parent))
            {
                continue;
            }

            checkpoints.Add(checkpoint);
        }

        return checkpoints;
    }

    public override async ValueTask<CheckpointInfo> CreateCheckpointAsync(string runId, JsonElement value, CheckpointInfo? parent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        // Each checkpoint gets an independent key and is fully self-describing in JSON.
        var checkpoint = new CheckpointInfo(runId, Guid.NewGuid().ToString());
        return await CreateCheckpointAsync(checkpoint, value, parent);
    }

    public async ValueTask<CheckpointInfo> CreateCheckpointAsync(
        CheckpointInfo checkpoint,
        JsonElement value,
        CheckpointInfo? parent = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint.CheckpointId);

        var payload = SerializeCheckpointPayload(checkpoint.SessionId, checkpoint.CheckpointId, value, parent);
        var blob = _containerClient.GetBlobClient(GetCheckpointBlobName(checkpoint.SessionId, checkpoint.CheckpointId));

        await blob.UploadAsync(BinaryData.FromString(payload), overwrite: true, cancellationToken);
        return checkpoint;
    }

    public override async ValueTask<JsonElement> RetrieveCheckpointAsync(string runId, CheckpointInfo key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var blob = _containerClient.GetBlobClient(GetCheckpointBlobName(runId, key.CheckpointId));
        if (!await blob.ExistsAsync())
        {
            throw new KeyNotFoundException($"Checkpoint '{key.CheckpointId}' not found for run '{runId}' in container '{_containerClient.Name}'.");
        }

        var downloaded = await blob.DownloadContentAsync();

        // Validate run/checkpoint identity before returning payload content.
        var payload = TryDeserializeCheckpointPayload(downloaded.Value.Content.ToString(), runId, key.CheckpointId);
        if (payload is null)
        {
            throw new KeyNotFoundException($"Checkpoint '{key.CheckpointId}' not found for run '{runId}' in container '{_containerClient.Name}'.");
        }

        return payload.Value;
    }

    #endregion

    #region IMutableJsonCheckpointStore implementation

    public async ValueTask<bool> TryUpdateCheckpointPayloadAsync(
        CheckpointInfo checkpoint,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var blob = _containerClient.GetBlobClient(GetCheckpointBlobName(checkpoint.SessionId, checkpoint.CheckpointId));
        if (!await blob.ExistsAsync(cancellationToken))
        {
            return false;
        }

        try
        {
            // Preserve original parent linkage while replacing payload and recalculating checksum.
            var downloaded = await blob.DownloadContentAsync(cancellationToken);
            var root = downloaded.Value.Content.ToObjectFromJson<JsonElement>();
            var parent = TryDeserializeParent(root);
            var serialized = SerializeCheckpointPayload(checkpoint.SessionId, checkpoint.CheckpointId, payload, parent);
            await blob.UploadAsync(BinaryData.FromString(serialized), overwrite: true, cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region Blob naming helpers

    private string GetCheckpointPrefix(string runId)
    {
        var root = string.IsNullOrWhiteSpace(_blobPrefix) ? "checkpoints" : $"{_blobPrefix}/checkpoints";
        return $"{root}/{SanitizeSegment(runId)}/";
    }

    private string GetCheckpointBlobName(string runId, string checkpointId)
        => $"{GetCheckpointPrefix(runId)}{SanitizeSegment(checkpointId)}.checkpoint.json";

    private static string SanitizeSegment(string value) => Uri.EscapeDataString(value);

    #endregion

    #region Checkpoint metadata parsing

    private CheckpointInfo? TryParseCheckpointInfoFromBlobName(string blobName, string runId)
    {
        var prefix = GetCheckpointPrefix(runId);
        if (!blobName.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var fileName = blobName[prefix.Length..];
        if (!fileName.EndsWith(CheckpointFileSuffix, StringComparison.Ordinal))
        {
            return null;
        }

        var encodedCheckpointId = fileName[..^CheckpointFileSuffix.Length];
        if (string.IsNullOrWhiteSpace(encodedCheckpointId))
        {
            return null;
        }

        var checkpointId = Uri.UnescapeDataString(encodedCheckpointId);
        if (string.IsNullOrWhiteSpace(checkpointId))
        {
            return null;
        }

        return new CheckpointInfo(runId, checkpointId);
    }

    private static bool ParentMatches(CheckpointInfo expectedParent, CheckpointInfo? actualParent)
    {
        if (actualParent is null)
        {
            return false;
        }

        return string.Equals(expectedParent.SessionId, actualParent.SessionId, StringComparison.Ordinal)
               && string.Equals(expectedParent.CheckpointId, actualParent.CheckpointId, StringComparison.Ordinal);
    }

    #endregion

    #region JSON serialization and deserialization

    private static CheckpointEnvelopeInfo? TryDeserializeCheckpointEnvelope(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var runId = GetStringProperty(root, "runId") ?? GetStringProperty(root, "RunId");
            var checkpointId = GetStringProperty(root, "checkpointId") ?? GetStringProperty(root, "CheckpointId");
            if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(checkpointId)) return null;

            return new CheckpointEnvelopeInfo
            {
                Checkpoint = new CheckpointInfo(runId, checkpointId),
                Parent = TryDeserializeParent(root)
            };
        }
        catch
        {
            return null;
        }
    }

    private static JsonElement? TryDeserializeCheckpointPayload(string json, string runId, string checkpointId)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var r = GetStringProperty(root, "runId") ?? GetStringProperty(root, "RunId");
            var c = GetStringProperty(root, "checkpointId") ?? GetStringProperty(root, "CheckpointId");
            if (!string.Equals(r, runId, StringComparison.Ordinal)) return null;
            if (!string.Equals(c, checkpointId, StringComparison.Ordinal)) return null;
            if (!root.TryGetProperty("payload", out var payload)) return null;

            return payload.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string? GetStringProperty(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    private static CheckpointInfo? TryDeserializeParent(JsonElement root)
    {
        var parentRunId = GetStringProperty(root, "parentRunId") ?? GetStringProperty(root, "ParentRunId");
        var parentCheckpointId = GetStringProperty(root, "parentCheckpointId") ?? GetStringProperty(root, "ParentCheckpointId");
        if (string.IsNullOrWhiteSpace(parentRunId) || string.IsNullOrWhiteSpace(parentCheckpointId))
        {
            return null;
        }

        return new CheckpointInfo(parentRunId, parentCheckpointId);
    }

    private static string SerializeCheckpointPayload(string runId, string checkpointId, JsonElement payload, CheckpointInfo? parent)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, SkipValidation = false });

        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", SchemaVersion);
        writer.WriteString("createdUtc", DateTimeOffset.UtcNow);
        writer.WriteString("runId", runId);
        writer.WriteString("checkpointId", checkpointId);

        if (parent is not null)
        {
            writer.WriteString("parentRunId", parent.SessionId);
            writer.WriteString("parentCheckpointId", parent.CheckpointId);
        }

        var payloadRaw = payload.GetRawText();

        // Store checksum to support diagnostics and corruption detection without re-parsing payload semantics.
        writer.WriteString("payloadSha256", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadRaw))));

        writer.WritePropertyName("payload");
        writer.WriteRawValue(payloadRaw, skipInputValidation: true);
        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    #endregion

    #region Internal models

    private sealed class CheckpointEnvelopeInfo
    {
        public required CheckpointInfo Checkpoint { get; init; }
        public CheckpointInfo? Parent { get; init; }
    }

    #endregion
}
