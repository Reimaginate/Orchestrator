using System.Text.Json.Nodes;
using Reimaginate.Orchestrator.Common.Models;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowEventDispatch;

public sealed record ListeningWorkflowTypeContext(string WorkflowType, IReadOnlyList<(WorkflowEventBinding Binding, ProcessEventEnvelope EventEnvelope)> MatchingBindings, bool CanResume,
    bool CanStart, JsonObject StartInput);