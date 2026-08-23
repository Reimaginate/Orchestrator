using System.Text.Json;
using Microsoft.Agents.AI.Workflows;

namespace Reimaginate.Orchestrator.Common.Stores.Checkpoints;

public interface IExplicitCheckpointStore
{
    ValueTask<CheckpointInfo> CreateCheckpointAsync(
        CheckpointInfo checkpoint,
        JsonElement value,
        CheckpointInfo? parent = null,
        CancellationToken cancellationToken = default);
}
