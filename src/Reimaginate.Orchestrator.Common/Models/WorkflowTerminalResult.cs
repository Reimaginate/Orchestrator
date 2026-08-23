namespace Reimaginate.Orchestrator.Common.Models;

internal sealed record WorkflowTerminalResult(
    string Status,
    object? FinalOutput,
    string? Reason);
