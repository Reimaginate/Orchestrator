using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

namespace Reimaginate.Orchestrator.Common.Stores.Checkpoints;

public sealed class FileSystemCheckpointStore : JsonCheckpointStore, IExplicitCheckpointStore, IDisposable
{
    #region State and configuration

    private const int SchemaVersion = 1;
    private readonly object _gate = new();
    private long _lastCheckpointTimestamp;
    private int _lastCheckpointSequence;
    private int _isDisposed;

    internal DirectoryInfo Directory { get; }

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        SkipValidation = false
    };

    #endregion

    #region Construction

    public FileSystemCheckpointStore(DirectoryInfo directory)
    {
        Directory = directory ?? throw new ArgumentNullException(nameof(directory));
        if (!Directory.Exists) Directory.Create();
    }

    #endregion

    #region Checkpoint store operations

    public override ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string runId, CheckpointInfo? withParent = null)
    {
        CheckDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var runDirectoryPath = GetRunDirectoryPath(runId);
        if (!System.IO.Directory.Exists(runDirectoryPath))
            return new(Array.Empty<CheckpointInfo>());

        var results = new List<CheckpointInfo>();

        foreach (var checkpointFilePath in EnumerateCheckpointFiles(runId))
        {
            // Read lightweight envelope metadata so index retrieval can avoid loading payload JSON.
            var info = TryDeserializeCheckpointEnvelopeFromFile(checkpointFilePath);
            if (info is { Checkpoint: { } checkpoint }
                && string.Equals(checkpoint.SessionId, runId, StringComparison.Ordinal)
                && ParentMatches(withParent, info.Parent))
            {
                results.Add(checkpoint);
            }
        }

        return new(results);
    }

    public override ValueTask<CheckpointInfo> CreateCheckpointAsync(string runId, JsonElement value, CheckpointInfo? parent = null)
    {
        CheckDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        try
        {
            CheckpointInfo key;

            lock (_gate)
            {
                key = new CheckpointInfo(runId, GenerateCheckpointId());

                var checkpointPath = GetCheckpointFilePath(runId, key.CheckpointId);
                var payload = SerializeCheckpointPayload(runId, key.CheckpointId, value, parent);
                var runDirectoryPath = GetRunDirectoryPath(runId);

                // Ensure per-run directory exists before writing the new checkpoint document.
                System.IO.Directory.CreateDirectory(runDirectoryPath);
                File.WriteAllText(checkpointPath, payload, Encoding.UTF8);
            }

            return new(key);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not create checkpoint in store at '{Directory.FullName}'.", ex);
        }
    }

    public ValueTask<CheckpointInfo> CreateCheckpointAsync(
        CheckpointInfo checkpoint,
        JsonElement value,
        CheckpointInfo? parent = null,
        CancellationToken cancellationToken = default)
    {
        CheckDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint.CheckpointId);

        try
        {
            lock (_gate)
            {
                var checkpointPath = GetCheckpointFilePath(checkpoint.SessionId, checkpoint.CheckpointId);
                var payload = SerializeCheckpointPayload(checkpoint.SessionId, checkpoint.CheckpointId, value, parent);
                var runDirectoryPath = GetRunDirectoryPath(checkpoint.SessionId);

                System.IO.Directory.CreateDirectory(runDirectoryPath);
                File.WriteAllText(checkpointPath, payload, Encoding.UTF8);
            }

            return new(checkpoint);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not create checkpoint in store at '{Directory.FullName}'.", ex);
        }
    }

    public override ValueTask<JsonElement> RetrieveCheckpointAsync(string runId, CheckpointInfo key)
    {
        CheckDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var path = GetCheckpointFilePath(runId, key.CheckpointId);
        if (!File.Exists(path))
            throw new KeyNotFoundException($"Checkpoint '{key.CheckpointId}' not found for run '{runId}' in '{Directory.FullName}'.");

        var payload = TryDeserializeCheckpointPayloadFromFile(path, runId, key.CheckpointId);
        if (payload is null)
            throw new KeyNotFoundException($"Checkpoint '{key.CheckpointId}' not found for run '{runId}' in '{Directory.FullName}'.");

        return new(payload.Value);
    }


    public async ValueTask<bool> TryUpdateCheckpointPayloadAsync(
        CheckpointInfo checkpoint,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        CheckDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            string? envelopeText = null;

            lock (_gate)
            {
                var checkpointPath = GetCheckpointFilePath(checkpoint.SessionId, checkpoint.CheckpointId);
                if (!File.Exists(checkpointPath))
                {
                    return false;
                }

                var existing = File.ReadAllText(checkpointPath, Encoding.UTF8);
                using var doc = JsonDocument.Parse(existing);
                var root = doc.RootElement;

                var runId = GetStringProperty(root, "runId") ?? GetStringProperty(root, "RunId");
                var checkpointId = GetStringProperty(root, "checkpointId") ?? GetStringProperty(root, "CheckpointId");
                if (!string.Equals(runId, checkpoint.SessionId, StringComparison.Ordinal)
                    || !string.Equals(checkpointId, checkpoint.CheckpointId, StringComparison.Ordinal))
                {
                    // Guard against stale/mismatched files if path sanitization ever produces collisions.
                    return false;
                }

                var parent = TryDeserializeParent(root);
                envelopeText = SerializeCheckpointPayload(checkpoint.SessionId, checkpoint.CheckpointId, payload, parent);
                File.WriteAllText(checkpointPath, envelopeText, Encoding.UTF8);
            }

            await ValueTask.CompletedTask;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _isDisposed, 1);
    }

    #endregion

    #region Path and state guards

    private void CheckDisposed()
    {
        if (Volatile.Read(ref _isDisposed) != 0)
            throw new ObjectDisposedException($"{nameof(FileSystemCheckpointStore)}({Directory.FullName})");
    }

    private string GetRunDirectoryPath(string runId)
        => Path.Combine(Directory.FullName, SanitizeFileName(runId));

    private string GetCheckpointFilePath(string runId, string checkpointId)
        => Path.Combine(GetRunDirectoryPath(runId), $"{SanitizeFileName(checkpointId)}.checkpoint.json");

    private IEnumerable<string> EnumerateCheckpointFiles(string runId)
    {
        var runDirectoryPath = GetRunDirectoryPath(runId);
        return System.IO.Directory.EnumerateFiles(runDirectoryPath, "*.checkpoint.json", SearchOption.TopDirectoryOnly);
    }

    private static string SanitizeFileName(string name)
    {
        // Keep each checkpoint addressable on any supported filesystem.
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    #endregion

    #region Checkpoint identity and matching

    private static bool ParentMatches(CheckpointInfo? expectedParent, CheckpointInfo? actualParent)
    {
        if (expectedParent is null)
        {
            return true;
        }

        if (actualParent is null)
        {
            return false;
        }

        return string.Equals(expectedParent.SessionId, actualParent.SessionId, StringComparison.Ordinal)
               && string.Equals(expectedParent.CheckpointId, actualParent.CheckpointId, StringComparison.Ordinal);
    }

    private string GenerateCheckpointId()
    {
        var now = DateTimeOffset.UtcNow;
        var timestamp = now.ToUnixTimeMilliseconds();

        // Monotonic sequence prevents collisions when multiple checkpoints share the same millisecond.
        if (timestamp == _lastCheckpointTimestamp)
        {
            _lastCheckpointSequence++;
        }
        else
        {
            _lastCheckpointTimestamp = timestamp;
            _lastCheckpointSequence = 0;
        }

        return $"{now:yyyyMMddHHmmssfff}-{_lastCheckpointSequence:D4}";
    }

    #endregion

    #region Serialization helpers

    private static CheckpointEnvelopeInfo? TryDeserializeCheckpointEnvelopeFromFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var runId = GetStringProperty(root, "runId") ?? GetStringProperty(root, "RunId");
            var checkpointId = GetStringProperty(root, "checkpointId") ?? GetStringProperty(root, "CheckpointId");

            if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(checkpointId))
                return null;

            return new CheckpointEnvelopeInfo
            {
                Checkpoint = new CheckpointInfo(runId!, checkpointId!),
                Parent = TryDeserializeParent(root)
            };
        }
        catch
        {
            return null;
        }
    }

    private static JsonElement? TryDeserializeCheckpointPayloadFromFile(string path, string runId, string checkpointId)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var r = GetStringProperty(root, "runId") ?? GetStringProperty(root, "RunId");
            var c = GetStringProperty(root, "checkpointId") ?? GetStringProperty(root, "CheckpointId");

            if (!string.Equals(r, runId, StringComparison.Ordinal)) return null;
            if (!string.Equals(c, checkpointId, StringComparison.Ordinal)) return null;

            if (!root.TryGetProperty("payload", out var payload))
                return null;

            // Clone to detach from the JsonDocument lifetime before returning.
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
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
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

            // Persist a payload hash to support diagnostics and future integrity checks.
            var payloadRaw = payload.GetRawText();
            writer.WriteString("payloadSha256", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadRaw))));

            writer.WritePropertyName("payload");
            payload.WriteTo(writer);

            writer.WriteEndObject();
            writer.Flush();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    #endregion

    #region Internal DTOs

    private sealed class CheckpointEnvelopeInfo
    {
        public required CheckpointInfo Checkpoint { get; init; }
        public CheckpointInfo? Parent { get; init; }
    }

    #endregion
}
