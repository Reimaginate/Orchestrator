using Reimaginate.Mediator;
using Reimaginate.Orchestrator.Common.Requests.External.StartWorkflow;
using Reimaginate.Orchestrator.ReferenceHost.Actions;

namespace Reimaginate.Orchestrator.ReferenceHost;

[Mediator]
[ScanAssembly(typeof(StartWorkflowRequest))]
[ScanAssembly(typeof(EchoRequest))]
public partial class Mediator;
