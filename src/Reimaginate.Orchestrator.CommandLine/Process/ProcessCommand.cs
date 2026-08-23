using Reimaginate.CLI.Base.Abstractions;

namespace Reimaginate.Orchestrator.CommandLine.Process;

public class ProcessCommand(IServiceProvider serviceProvider) : TopLevelCommand("process", serviceProvider);
