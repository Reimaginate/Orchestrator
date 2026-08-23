using Reimaginate.Orchestrator.Abstractions;

namespace Reimaginate.Orchestrator.Common.Requests.Internal.RegisterWorkflowCorrelation;

public class RegisterWorkflowCorrelationResponse : Result
{
    public bool CorrelationAdded { get; set; }
}
