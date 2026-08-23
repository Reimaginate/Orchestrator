using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Options;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowDefinitions;

public sealed class WorkflowBuilderAdapterFactory(IOptions<WorkflowBuilderAdapterOptions> options) : IWorkflowBuilderAdapterFactory
{
    public WorkflowBuilderAdapterFactory()
        : this(Options.Create(new WorkflowBuilderAdapterOptions()))
    {
    }

    public IWorkflowBuilderAdapter Create(ExecutorBinding start)
    {
        if (options.Value.UseLegacyReflectionAdapter)
        {
            return new LegacyReflectionWorkflowBuilderAdapter(start);
        }

        return new MafWorkflowBuilderAdapter(start);
    }
}
