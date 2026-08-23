using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

namespace Reimaginate.Orchestrator.Common.Stores.Checkpoints;

public sealed class InMemoryCheckpointStore : JsonCheckpointStore, IExplicitCheckpointStore
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, CheckpointEntry>> _runs = new(StringComparer.Ordinal);

    public override ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string runId, CheckpointInfo? withParent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        if (!_runs.TryGetValue(runId, out var runCheckpoints))
        {
            return new(Array.Empty<CheckpointInfo>());
        }

        var checkpoints = withParent is null
            ? runCheckpoints.Values.Select(x => x.Checkpoint).ToArray()
            : runCheckpoints.Values.Where(x => ParentMatches(withParent, x.Parent)).Select(x => x.Checkpoint).ToArray();

        return new(checkpoints);
    }

    public override ValueTask<CheckpointInfo> CreateCheckpointAsync(string runId, JsonElement value, CheckpointInfo? parent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var checkpoint = new CheckpointInfo(runId, Guid.NewGuid().ToString("N"));
        return CreateCheckpointAsync(checkpoint, value, parent);
    }

    public ValueTask<CheckpointInfo> CreateCheckpointAsync(
        CheckpointInfo checkpoint,
        JsonElement value,
        CheckpointInfo? parent = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint.CheckpointId);

        var runId = checkpoint.SessionId;
        var runCheckpoints = _runs.GetOrAdd(runId, _ => new ConcurrentDictionary<string, CheckpointEntry>(StringComparer.Ordinal));

        runCheckpoints[checkpoint.CheckpointId] = new CheckpointEntry(checkpoint, value.Clone(), parent);

        return new(checkpoint);
    }

    public override ValueTask<JsonElement> RetrieveCheckpointAsync(string runId, CheckpointInfo key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        if (_runs.TryGetValue(runId, out var runCheckpoints)
            && runCheckpoints.TryGetValue(key.CheckpointId, out var entry))
        {
            return new(entry.Payload.Clone());
        }

        throw new KeyNotFoundException($"Checkpoint '{key.CheckpointId}' not found for run '{runId}'.");
    }

    public ValueTask<bool> TryUpdateCheckpointPayloadAsync(
        CheckpointInfo checkpoint,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_runs.TryGetValue(checkpoint.SessionId, out var runCheckpoints)
            && runCheckpoints.TryGetValue(checkpoint.CheckpointId, out var current))
        {
            var updated = current with { Payload = payload.Clone() };
            var saved = runCheckpoints.TryUpdate(checkpoint.CheckpointId, updated, current);
            return new(saved);
        }

        return new(false);
    }

    private static bool ParentMatches(CheckpointInfo expectedParent, CheckpointInfo? actualParent)
        => actualParent is not null
           && string.Equals(expectedParent.SessionId, actualParent.SessionId, StringComparison.Ordinal)
           && string.Equals(expectedParent.CheckpointId, actualParent.CheckpointId, StringComparison.Ordinal);

    private sealed record CheckpointEntry(CheckpointInfo Checkpoint, JsonElement Payload, CheckpointInfo? Parent);
}
