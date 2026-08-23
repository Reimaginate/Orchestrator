using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.DSL;

namespace Reimaginate.Orchestrator.Common.Requests.Internal.ResolveAndLoadWorkflowDefinition;

public class ResolveAndLoadWorkflowDefinitionResponse : Result
{
    public WorkflowAst Ast { get; set; } = null!;

    public WorkflowDefinition Definition { get; set; } = null!;
}
