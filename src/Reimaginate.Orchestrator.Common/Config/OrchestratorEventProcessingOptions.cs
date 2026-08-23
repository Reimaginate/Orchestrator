namespace Reimaginate.Orchestrator.Common.Config;

public sealed class OrchestratorEventProcessingOptions
{
    public string? QueueName { get; set; }
    public string? MaxConcurrency { get; set; }
}
