using System.Text.Json.Nodes;

namespace Reimaginate.Orchestrator.Common.Helpers;

public interface IWorkflowEnvironmentProvider
{
    JsonObject BuildEnvironment();
    JsonObject BuildEnvironment(string? workflowType) => BuildEnvironment();
}
