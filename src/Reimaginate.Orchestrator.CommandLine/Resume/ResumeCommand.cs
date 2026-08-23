using Reimaginate.CLI.Base.Abstractions;

namespace Reimaginate.Orchestrator.CommandLine.Resume;

public class ResumeCommand(IServiceProvider serviceProvider) : TopLevelCommand("resume", serviceProvider);
