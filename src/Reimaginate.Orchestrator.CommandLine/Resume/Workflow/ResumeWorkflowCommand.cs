using System.CommandLine;
using System.Diagnostics;
using Reimaginate.CLI.Base.Abstractions;
using Reimaginate.CLI.Base.Attributes;
using Reimaginate.Mediator;
using Reimaginate.Mediator.Abstractions;
using Reimaginate.Orchestrator.Common;
using Reimaginate.Orchestrator.Common.Diagnostics;
using Reimaginate.Orchestrator.Common.Requests.External.ResumeWorkflow;

namespace Reimaginate.Orchestrator.CommandLine.Resume.Workflow;

[Argument("workflowType", typeof(string), required: true, allowMultiple: false, description: "Workflow type")]
[Argument("workflowInstanceId", typeof(string), required: true, allowMultiple: false, description: "Workflow instance id")]
[Option("input", typeof(string), required: false, description: "Workflow resume input as a raw string, JSON object, or comma-delimited key=value pairs; pair values recognize JSON booleans, numbers, null, and double-quoted strings; other text remains a string")]
[Option("checkpoints", typeof(string), required: false, description: WorkflowExecutionPolicyCommandOptions.CheckpointsDescription)]
[Option("workflow-instances", typeof(string), required: false, description: WorkflowExecutionPolicyCommandOptions.WorkflowInstancesDescription)]
[Option("log-steps", typeof(bool), required: false, description: "Write each authored workflow step to the console as it is entered")]
public class ResumeWorkflowCommand : SubCommand<ResumeCommand>
{
    private readonly IMediator _mediator;

    public ResumeWorkflowCommand(IServiceProvider serviceProvider, IMediator mediator) : base("workflow", serviceProvider)
    {
        _mediator = mediator;

        SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var workflowType = parseResult.GetValue<string>("workflowType");
            var workflowInstanceId = parseResult.GetValue<string>("workflowInstanceId");
            var input = parseResult.GetValue<string>("--input");
            var checkpoints = parseResult.GetValue<string>("--checkpoints");
            var workflowInstances = parseResult.GetValue<string>("--workflow-instances");
            var logSteps = parseResult.GetValue<bool>("--log-steps");
            return await HandleCommand(workflowType!, workflowInstanceId!, input, checkpoints, workflowInstances, logSteps, cancellationToken);
        });
    }

    private async Task<int> HandleCommand(string workflowType, string workflowInstanceId, string? input, string? checkpoints, string? workflowInstances, bool logSteps, CancellationToken cancellationToken = default)
    {
        if (!WorkflowExecutionPolicyCommandOptions.TryCreate(checkpoints, workflowInstances, out var executionPolicyOverride, out var failureReason))
        {
            Console.WriteLine($"Error: {failureReason}");
            return 1;
        }

        var correlationId = Guid.NewGuid().ToString();
        using var activity = DiagnosticConfig.ActivitySource.StartActivity(ActivityKind.Consumer);
        activity?.SetTag(DiagnosticConstants.CorrelationId, correlationId);
        Console.WriteLine($"CorrelationId: {correlationId}");

        using var stepLogging = WorkflowStepConsoleContext.Begin(logSteps);
        var (response, err) = await _mediator.TrySend(new ResumeWorkflowRequest
        {
            WorkflowType = workflowType,
            WorkflowInstanceId = workflowInstanceId,
            Input = WorkflowInputParser.Parse(input),
            ExecutionPolicyOverride = executionPolicyOverride
        }, cancellationToken);

        if (response?.Success ?? false)
        {
            Console.WriteLine($"WorkflowInstanceId: {workflowInstanceId}");
            if (!string.IsNullOrWhiteSpace(response.DiagnosticsPath))
            {
                Console.WriteLine($"DiagnosticsPath: {response.DiagnosticsPath}");
            }
            WorkflowCompletionOutputWriter.WriteIfPresent(workflowType, workflowInstanceId, response.FinalOutput);
            return 0;
        }

        Console.WriteLine($"Failed to resume workflow '{workflowInstanceId}': {response?.FailureReason ?? err?.Message}");
        WorkflowFailureOutputWriter.WriteIfPresent(response?.WorkflowInstanceId, response?.DiagnosticsPath, response?.FailureDetails);
        return 1;
    }
}
