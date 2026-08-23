# Reimaginate.Orchestrator.Redis

Orchestrator is Reimaginate's opinionated YAML workflow and durability layer built on the [Microsoft Agent Framework Workflows runtime](https://github.com/microsoft/agent-framework).

Redis-backed workflow instance locking integration for Orchestrator.

Register it after core Orchestrator services:

```csharp
services.AddOrchestratorServices(configuration.GetSection("Orchestrator"), typeof(MyWorkflowActionResolver));
services.AddRedisWorkflowInstanceLocks(redisConnectionMultiplexer);
```

Locks are renewed while the lease is held. `LeaseDuration` is the Redis key TTL, not the maximum workflow runtime. Set `RenewalInterval` shorter than `LeaseDuration`; when omitted it defaults to roughly one third of the lease. If renewal fails repeatedly or the lock token no longer matches, the lease `LostToken` is cancelled so long-running workflow execution can stop safely.

Redis command timeouts are controlled by the StackExchange.Redis connection string, for example `asyncTimeout`, `syncTimeout`, and `connectTimeout`.
