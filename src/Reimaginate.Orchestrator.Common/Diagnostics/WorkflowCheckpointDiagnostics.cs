using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

namespace Reimaginate.Orchestrator.Common.Diagnostics;

internal static class WorkflowCheckpointDiagnostics
{
    public static async ValueTask<JsonObject> BuildSummaryAsync(JsonCheckpointStore store, CheckpointInfo checkpoint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var payload = await store.RetrieveCheckpointAsync(checkpoint.SessionId, checkpoint);
        var payloadRaw = payload.GetRawText();

        return new JsonObject
        {
            ["runId"] = checkpoint.SessionId,
            ["checkpointId"] = checkpoint.CheckpointId,
            ["capturedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["payloadSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadRaw))),
            ["payloadKind"] = payload.ValueKind.ToString(),
            ["hasRunnerData"] = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("runnerData", out _),
            ["hasWaitState"] = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("__wait", out _),
            ["payload"] = JsonNode.Parse(payloadRaw)
        };
    }
}
