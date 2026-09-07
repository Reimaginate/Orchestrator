namespace Reimaginate.Orchestrator.CommandLine.Config;

public sealed class OrchestratorCommandLineOptions
{
    public const string SectionName = "Orchestrator:CommandLine";

    /// <summary>
    /// Writes the final output envelope after a successful start or resume when output is present.
    /// </summary>
    public bool EmitFinalOutput { get; set; } = true;
}
