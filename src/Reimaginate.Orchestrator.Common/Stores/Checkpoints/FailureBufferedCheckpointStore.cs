using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

namespace Reimaginate.Orchestrator.Common.Stores.Checkpoints;

public sealed class FailureBufferedCheckpointStore(JsonCheckpointStore durableStore) : JsonCheckpointStore
{
    private readonly JsonCheckpointStore _durableStore = durableStore ?? throw new ArgumentNullException(nameof(durableStore));
    private readonly object _gate = new();
    private readonly List<CheckpointEntry> _ordered = [];
    private readonly Dictionary<string, Dictionary<string, CheckpointEntry>> _runs = new(StringComparer.Ordinal);
    private bool _discarded;
    private bool _flushed;

    public IReadOnlyList<CheckpointInfo> BufferedCheckpoints
    {
        get
        {
            lock (_gate)
            {
                return _ordered.Select(entry => entry.Checkpoint).ToArray();
            }
        }
    }

    public override async ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string runId, CheckpointInfo? withParent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var durableCheckpoints = await _durableStore.RetrieveIndexAsync(runId, withParent);
        CheckpointInfo[] bufferedCheckpoints;

        lock (_gate)
        {
            bufferedCheckpoints = !_runs.TryGetValue(runId, out var runCheckpoints)
                ? []
                : runCheckpoints.Values
                    .Where(entry => withParent is null || ParentMatches(withParent, entry.Parent))
                    .Select(entry => entry.Checkpoint)
                    .ToArray();
        }

        return durableCheckpoints
            .Concat(bufferedCheckpoints)
            .DistinctBy(checkpoint => $"{checkpoint.SessionId}:{checkpoint.CheckpointId}", StringComparer.Ordinal)
            .ToArray();
    }

    public override ValueTask<CheckpointInfo> CreateCheckpointAsync(string runId, JsonElement value, CheckpointInfo? parent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        lock (_gate)
        {
            if (_discarded)
            {
                throw new InvalidOperationException("Cannot create checkpoints after failure-buffered checkpoint store has been discarded.");
            }

            var checkpoint = new CheckpointInfo(runId, Guid.NewGuid().ToString("N"));
            var entry = new CheckpointEntry(checkpoint, value.Clone(), parent);
            var runCheckpoints = GetOrCreateRun(runId);

            runCheckpoints[checkpoint.CheckpointId] = entry;
            _ordered.Add(entry);

            return new(checkpoint);
        }
    }

    public override async ValueTask<JsonElement> RetrieveCheckpointAsync(string runId, CheckpointInfo key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        lock (_gate)
        {
            if (_runs.TryGetValue(runId, out var runCheckpoints)
                && runCheckpoints.TryGetValue(key.CheckpointId, out var entry))
            {
                return entry.Payload.Clone();
            }
        }

        return await _durableStore.RetrieveCheckpointAsync(runId, key);
    }

    public async ValueTask FlushToDurableStoreAsync(CancellationToken cancellationToken = default)
    {
        CheckpointEntry[] entries;

        lock (_gate)
        {
            if (_flushed)
            {
                return;
            }

            _flushed = true;
            entries = _ordered.ToArray();
        }

        if (entries.Length == 0)
        {
            return;
        }

        if (_durableStore is not IExplicitCheckpointStore explicitStore)
        {
            throw new NotSupportedException($"{_durableStore.GetType().FullName} does not support explicit checkpoint writes required by failure checkpointing mode.");
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await explicitStore.CreateCheckpointAsync(entry.Checkpoint, entry.Payload, entry.Parent, cancellationToken);
        }
    }

    public void Discard()
    {
        lock (_gate)
        {
            _discarded = true;
            _ordered.Clear();
            _runs.Clear();
        }
    }

    private Dictionary<string, CheckpointEntry> GetOrCreateRun(string runId)
    {
        if (!_runs.TryGetValue(runId, out var runCheckpoints))
        {
            runCheckpoints = new Dictionary<string, CheckpointEntry>(StringComparer.Ordinal);
            _runs[runId] = runCheckpoints;
        }

        return runCheckpoints;
    }

    private static bool ParentMatches(CheckpointInfo expectedParent, CheckpointInfo? actualParent)
        => actualParent is not null
           && string.Equals(expectedParent.SessionId, actualParent.SessionId, StringComparison.Ordinal)
           && string.Equals(expectedParent.CheckpointId, actualParent.CheckpointId, StringComparison.Ordinal);

    private sealed record CheckpointEntry(CheckpointInfo Checkpoint, JsonElement Payload, CheckpointInfo? Parent);
}
