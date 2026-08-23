using Microsoft.Agents.AI.Workflows.Checkpointing;

namespace Reimaginate.Orchestrator.Abstractions;

public interface ICheckpointStorage
{
    JsonCheckpointStore CreateStore();
}
