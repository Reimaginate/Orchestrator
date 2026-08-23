using Reimaginate.Mediator;
using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.ReferenceHost.Actions;

[WorkflowAction("Echo")]
public sealed class EchoRequest : IRequest<EchoResponse>
{
    public string Text { get; set; } = string.Empty;
}
