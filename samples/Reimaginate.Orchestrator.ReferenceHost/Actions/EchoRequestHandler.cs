using Reimaginate.Mediator;

namespace Reimaginate.Orchestrator.ReferenceHost.Actions;

public sealed class EchoRequestHandler : IHandler<EchoRequest, EchoResponse>
{
    public Task<EchoResponse> HandleAsync(EchoRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new EchoResponse { Text = request.Text });
}
