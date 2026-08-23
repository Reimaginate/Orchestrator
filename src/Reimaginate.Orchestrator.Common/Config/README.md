# Configuration conventions

This folder is the core composition boundary for
`Reimaginate.Orchestrator.Common`.

Core runtime registration and adapter-neutral options belong here. Azure,
DataHub, Service Bus, Event Grid, and other external adapter composition belongs
in the corresponding adapter or customer host.

Call `AddOrchestratorServices(...)` to register the runtime. Add storage and
transport adapters explicitly. Without external stores, the runtime uses its
in-memory defaults.

For configuration details and examples, see the
[Orchestrator documentation](https://docs.reimaginate.online/orchestrator).
