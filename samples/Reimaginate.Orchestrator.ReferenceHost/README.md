# ReferenceHost

This sample composes the public Orchestrator runtime directly, registers a
minimal `Echo` workflow action, and uses in-memory storage by default.

Run `dotnet run -- --help` to inspect the available commands. Replace the
in-memory stores with Azure Storage registrations when durable persistence is
required.
