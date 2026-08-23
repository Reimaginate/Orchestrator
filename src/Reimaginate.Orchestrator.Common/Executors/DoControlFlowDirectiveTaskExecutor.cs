using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Workflows;

namespace Reimaginate.Orchestrator.Common.Executors;

internal sealed class DoControlFlowDirectiveTaskExecutor(string id) : Executor<JsonObject, JsonObject>(id)
{
    #region Route constants

    public const string ContinueRoute = "__do_continue";
    public const string ExitRoute = "__do_exit";
    public const string DirectivePropertyName = "__doControlFlow";

    #endregion

    #region Execution

    public override ValueTask<JsonObject> HandleAsync(JsonObject currentObject, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        // Keep the incoming payload intact and only append routing metadata.
        var output = (JsonObject)currentObject.DeepClone();
        var directive = output[DirectivePropertyName]?.GetValue<string>();

        output[SwitchTaskExecutor.SelectedRouteProperty] = string.Equals(directive, "break", StringComparison.OrdinalIgnoreCase)
            ? ExitRoute
            : ContinueRoute;

        return ValueTask.FromResult(output);
    }

    #endregion
}
