using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Workflows;
using Reimaginate.Orchestrator.Common.Diagnostics;

namespace Reimaginate.Orchestrator.Common.Executors;

internal sealed class WorkflowStepLoggingExecutor<TOutput>(
    string id,
    string stepName,
    Executor<JsonObject, TOutput> innerExecutor) : Executor<JsonObject, TOutput>(id)
{
    public override ValueTask<TOutput> HandleAsync(
        JsonObject message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        WorkflowStepConsoleContext.Write(stepName);
        return innerExecutor.HandleAsync(message, context, cancellationToken);
    }
}
