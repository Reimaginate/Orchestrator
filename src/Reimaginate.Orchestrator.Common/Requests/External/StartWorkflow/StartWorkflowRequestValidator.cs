using FluentValidation;

namespace Reimaginate.Orchestrator.Common.Requests.External.StartWorkflow;

public class StartWorkflowRequestValidator : AbstractValidator<StartWorkflowRequest>
{
    public StartWorkflowRequestValidator()
    {
    }
}
