using System.CommandLine;
using System.Diagnostics;
using Reimaginate.CLI.Base.Abstractions;
using Reimaginate.CLI.Base.Attributes;
using Reimaginate.Mediator;
using Reimaginate.Mediator.Abstractions;
using Reimaginate.Orchestrator.Common;
using Reimaginate.Orchestrator.Common.Diagnostics;
using Reimaginate.Orchestrator.Common.Requests.External.ProcessEvents;

namespace Reimaginate.Orchestrator.CommandLine.Process.Events;

[Option("queue-name", typeof(string), required: false, description: "Optional queue name override for event processing")]
[Option("max-concurrency", typeof(int?), required: false, description: "Optional maximum concurrent event workflow operations")]
[Option("dry-run", typeof(bool), required: false, description: "Inspect received events and release them without starting, resuming, completing, or dead-lettering messages")]
[Option("log-events", typeof(bool), required: false, description: "Write per-message event matching diagnostics to the console")]
[Option("checkpoints", typeof(string), required: false, description: WorkflowExecutionPolicyCommandOptions.CheckpointsDescription)]
[Option("workflow-instances", typeof(string), required: false, description: WorkflowExecutionPolicyCommandOptions.WorkflowInstancesDescription)]
[Option("log-steps", typeof(bool), required: false, description: "Write each authored workflow step to the console as it is entered")]
public class ProcessEventsCommand : SubCommand<ProcessCommand>
{
    private readonly IMediator _mediator;

    public ProcessEventsCommand(IServiceProvider serviceProvider, IMediator mediator) : base("events", serviceProvider)
    {
        _mediator = mediator;

        SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var queueName = parseResult.GetValue<string>("--queue-name");
            var maxConcurrency = parseResult.GetValue<int?>("--max-concurrency");
            var dryRun = parseResult.GetValue<bool>("--dry-run");
            var logEvents = parseResult.GetValue<bool>("--log-events");
            var checkpoints = parseResult.GetValue<string>("--checkpoints");
            var workflowInstances = parseResult.GetValue<string>("--workflow-instances");
            var logSteps = parseResult.GetValue<bool>("--log-steps");
            return await HandleCommand(queueName, maxConcurrency, dryRun, logEvents, checkpoints, workflowInstances, logSteps, cancellationToken);
        });
    }

    private async Task<int> HandleCommand(string? queueName, int? maxConcurrency, bool dryRun, bool logEvents, string? checkpoints, string? workflowInstances, bool logSteps, CancellationToken cancellationToken = default)
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
        var (response, err) = await _mediator.TrySend<ProcessEventsResponse>(new ProcessEventsRequest
        {
            QueueName = queueName,
            MaxConcurrency = maxConcurrency,
            ExecutionPolicyOverride = executionPolicyOverride,
            DryRun = dryRun,
            DiagnosticsMode = logEvents ? ProcessEventsDiagnosticsMode.Summary : ProcessEventsDiagnosticsMode.None
        }, cancellationToken);

        if (response?.Success ?? false) return 0;

        Console.WriteLine($"Error: {response?.FailureReason ?? err?.Message}");
        return 1;

    }
}
