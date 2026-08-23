using Reimaginate.Orchestrator.Common.Services.WorkflowDefinitions.Compilation;
using Reimaginate.Orchestrator.DSL;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowDefinitions;

internal static class WorkflowExtensionWrappingSupport
{
    public static bool SupportsJsonHookWrapping(TaskDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return definition switch
        {
            TerminateTaskDefinition => false,
            ListenTaskDefinition => false,
            _ => true
        };
    }

    public static bool SupportsJsonHookWrapping(WorkflowPlanNodeKind kind)
    {
        return kind switch
        {
            WorkflowPlanNodeKind.Terminate => false,
            WorkflowPlanNodeKind.Listen => false,
            _ => true
        };
    }
}
