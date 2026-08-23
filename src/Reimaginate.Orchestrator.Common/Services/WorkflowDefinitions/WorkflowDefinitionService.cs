using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Agents.AI.Workflows;
using Reimaginate.Mediator;
using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.Common.Helpers;
using Reimaginate.Orchestrator.Common.Services.WorkflowDefinitions.Compilation;
using Reimaginate.Orchestrator.DSL;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowDefinitions;

public class WorkflowDefinitionService(
    IMediator mediator,
    IWorkflowBuilderAdapterFactory workflowBuilderAdapterFactory,
    Channel<JsonObject> channel,
    IWorkflowEventEmitter workflowEventEmitter,
    IWorkflowActionResolver workflowActionResolver,
    IWorkflowEnvironmentProvider workflowEnvironmentProvider) : IWorkflowDefinitionService
{
    public async Task<WorkflowBuildArtifact> BuildAgentWorkflowAsync(string workflowType, WorkflowAst ast, WorkflowDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ast);
        ArgumentNullException.ThrowIfNull(definition);

        var semanticValidation = new WorkflowSemanticValidator().Validate(ast);
        if (!semanticValidation.IsSuccessful || semanticValidation.Workflow is null)
        {
            var semanticErrors = semanticValidation.Diagnostics
                .Select(diagnostic => diagnostic.Message)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            throw new InvalidOperationException(string.Join(Environment.NewLine, semanticErrors));
        }

        var expandedDefinition = await new WorkflowInlineExpander(mediator).ExpandAsync(workflowType, definition, cancellationToken);
        var expandedValidation = new WorkflowSemanticValidator().Validate(expandedDefinition);
        if (!expandedValidation.IsSuccessful || expandedValidation.Workflow is null)
        {
            var expandedErrors = expandedValidation.Diagnostics
                .Select(diagnostic => diagnostic.Message)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            throw new InvalidOperationException(string.Join(Environment.NewLine, expandedErrors));
        }

        var compilationResult = new WorkflowDefinitionCompiler().Compile(expandedValidation.Workflow);
        if (!compilationResult.IsSuccessful || compilationResult.Plan is null)
        {
            var errors = compilationResult.Diagnostics
                .Select(diagnostic => diagnostic.Message)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }

        var materializer = new WorkflowPlanMaterializer(mediator, workflowBuilderAdapterFactory, channel, workflowEventEmitter, workflowActionResolver, workflowEnvironmentProvider, workflowType);
        return new WorkflowBuildArtifact
        {
            Workflow = materializer.Materialize(compilationResult.Plan),
            DiagnosticsGraph = WorkflowDiagnosticsGraphBuilder.Build(string.IsNullOrWhiteSpace(workflowType) ? "workflow" : workflowType, expandedDefinition, compilationResult.Plan),
            Definition = expandedDefinition
        };
    }

    public List<WorkflowAwaitingEventDescriptor> BuildAwaitingEventDescriptors(WorkflowDefinition definition, string? checkpointId)
        => BuildAwaitingEventDescriptors(definition, checkpointId, []);

    public List<WorkflowAwaitingEventDescriptor> BuildAwaitingEventDescriptors(
        WorkflowDefinition definition,
        string? checkpointId,
        IReadOnlyCollection<string> activeTaskNames)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var descriptors = new List<WorkflowAwaitingEventDescriptor>();
        var filterActiveTasks = activeTaskNames.Count > 0;
        var activeTaskSet = filterActiveTasks
            ? activeTaskNames
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.Ordinal)
            : null;

        foreach (var (taskName, taskDefinition) in EnumerateTasks(definition.Do))
        {
            if (filterActiveTasks && (activeTaskSet is null || !activeTaskSet.Contains(taskName)))
            {
                continue;
            }

            if (taskDefinition is ListenTaskDefinition listenTask)
            {
                var targets = listenTask.ToAny.Count > 0
                    ? listenTask.ToAny
                    : [new ListenTargetDefinition()];

                foreach (var target in targets)
                {
                    descriptors.Add(new WorkflowAwaitingEventDescriptor
                    {
                        TaskName = taskName,
                        EventType = string.IsNullOrWhiteSpace(target.Type) ? "*" : target.Type,
                        FilterExpression = string.IsNullOrWhiteSpace(target.Filter) ? "true" : target.Filter,
                        CheckpointId = checkpointId
                    });
                }

                continue;
            }

            if (taskDefinition is WaitTaskDefinition)
            {
                descriptors.Add(new WorkflowAwaitingEventDescriptor
                {
                    TaskName = taskName,
                    EventType = "__timer__",
                    FilterExpression = "true",
                    CheckpointId = checkpointId
                });
            }
        }

        return descriptors;
    }

    private static IEnumerable<KeyValuePair<string, TaskDefinition>> EnumerateTasks(IDictionary<string, TaskDefinition> tasks)
    {
        foreach (var task in tasks)
        {
            yield return task;

            switch (task.Value)
            {
                case DoTaskDefinition doTask:
                    foreach (var nested in EnumerateTasks(doTask.Do))
                    {
                        yield return nested;
                    }

                    break;
                case ForkTaskDefinition forkTask:
                    foreach (var nested in forkTask.Branches.SelectMany(branch => EnumerateTasks(branch.Do)))
                    {
                        yield return nested;
                    }

                    break;
                case TryTaskDefinition tryTask:
                    foreach (var nested in EnumerateTasks(tryTask.Try))
                    {
                        yield return nested;
                    }

                    foreach (var nested in tryTask.Catch.SelectMany(c => EnumerateTasks(c.Do)))
                    {
                        yield return nested;
                    }

                    if (tryTask.Finally is not null)
                    {
                        foreach (var nested in EnumerateTasks(tryTask.Finally))
                        {
                            yield return nested;
                        }
                    }

                    break;
                case RunTaskDefinition runTask when runTask.Inline is not null:
                    foreach (var nested in EnumerateTasks(runTask.Inline.Do))
                    {
                        yield return nested;
                    }

                    break;
            }
        }
    }
}
