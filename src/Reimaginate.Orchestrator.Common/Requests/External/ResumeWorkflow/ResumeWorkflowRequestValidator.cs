using FluentValidation;

namespace Reimaginate.Orchestrator.Common.Requests.External.ResumeWorkflow;

public class ResumeWorkflowRequestValidator : AbstractValidator<ResumeWorkflowRequest>
{
    public ResumeWorkflowRequestValidator()
    {
        RuleFor(x => x.WorkflowInstanceId).NotEmpty();
    }
}
