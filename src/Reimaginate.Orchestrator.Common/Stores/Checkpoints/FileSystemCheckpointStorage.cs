using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.Common.Stores.Checkpoints;

public sealed class FileSystemCheckpointStorage(DirectoryInfo directory) : ICheckpointStorage
{

    private readonly DirectoryInfo _directory = directory ?? throw new ArgumentNullException(nameof(directory));

    public Microsoft.Agents.AI.Workflows.Checkpointing.JsonCheckpointStore CreateStore()
    {
        if (!_directory.Exists)
        {
            _directory.Create();
        }

        return new FileSystemCheckpointStore(_directory);
    }
}
