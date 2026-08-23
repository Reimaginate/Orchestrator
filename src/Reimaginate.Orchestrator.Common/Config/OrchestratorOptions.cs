namespace Reimaginate.Orchestrator.Common.Config;

public sealed class OrchestratorOptions
{
    public const string DefaultSectionName = "Orchestrator";

    public OrchestratorStorageOptions WorkflowCheckpointStorage { get; set; } = new();
    public OrchestratorStorageOptions WorkflowInstanceStorage { get; set; } = new();
    public OrchestratorStorageOptions WorkflowDefinitionStorage { get; set; } = new();
    public OrchestratorDiagnosticsOptions Diagnostics { get; set; } = new();
    public OrchestratorEventProcessingOptions EventProcessing { get; set; } = new();
    public OrchestratorWorkflowInstanceMutationOptions WorkflowInstanceMutation { get; set; } = new();
    public OrchestratorEnvironmentOptions Environment { get; set; } = new();
}
