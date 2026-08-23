using Reimaginate.CLI.Base.Abstractions;

namespace Reimaginate.Orchestrator.CommandLine.Start;

public class StartCommand(IServiceProvider serviceProvider) : TopLevelCommand("start", serviceProvider);
