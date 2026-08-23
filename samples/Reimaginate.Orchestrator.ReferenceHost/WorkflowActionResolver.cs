using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.ReferenceHost.Actions;

namespace Reimaginate.Orchestrator.ReferenceHost;

[WorkflowActionResolver]
[ScanAssembly(typeof(Reimaginate.Orchestrator.Common.Mediator))]
[ScanAssembly(typeof(EchoRequest))]
public partial class WorkflowActionResolver : IWorkflowActionResolver;
