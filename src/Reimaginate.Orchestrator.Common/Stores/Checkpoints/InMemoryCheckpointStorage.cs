using Microsoft.Agents.AI.Workflows.Checkpointing;
using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.Common.Stores.Checkpoints;

public sealed class InMemoryCheckpointStorage : ICheckpointStorage
{
    private readonly InMemoryCheckpointStore _store = new();

    public JsonCheckpointStore CreateStore() => _store;
}
