using Reimaginate.CLI.Base.Abstractions;

namespace Reimaginate.Orchestrator.CommandLine.Inspect;

public class InspectCommand(IServiceProvider serviceProvider) : TopLevelCommand("inspect", serviceProvider);
