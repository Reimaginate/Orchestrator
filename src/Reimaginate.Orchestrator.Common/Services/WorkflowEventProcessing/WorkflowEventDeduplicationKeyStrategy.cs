using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Reimaginate.Orchestrator.Common.Models;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowEventProcessing;

public sealed class WorkflowEventDeduplicationKeyStrategy : IWorkflowEventDeduplicationKeyStrategy
{
    public string BuildEventWorkflowProcessingKey(ProcessEventEnvelope envelope, string workflowType)
    {
        if (!string.IsNullOrWhiteSpace(envelope.EventId))
        {
            return $"event:{envelope.EventId}|wf:{workflowType}";
        }

        if (envelope.CorrelationKeys.Count > 0)
        {
            var correlationSet = string.Join("|", envelope.CorrelationKeys.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Name}:{x.Value}"));
            return $"corr:{correlationSet}|wf:{workflowType}";
        }

        if (!string.IsNullOrWhiteSpace(envelope.EventSource) && !string.IsNullOrWhiteSpace(envelope.EventSequence))
        {
            return $"seq:{envelope.EventSource}:{envelope.EventSequence}|wf:{workflowType}";
        }

        var payloadFingerprint = BuildPayloadFingerprint(envelope.Payload);
        return $"payload:{payloadFingerprint}|wf:{workflowType}";
    }

    public IReadOnlyCollection<string> BuildDeduplicationKeys(ProcessEventEnvelope envelope, string workflowInstanceId)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(envelope.EventId))
        {
            keys.Add($"id:{envelope.EventId}|wf:{workflowInstanceId}");
        }

        if (!string.IsNullOrWhiteSpace(envelope.EventSource)
            && !string.IsNullOrWhiteSpace(envelope.EventSequence)
            && !string.IsNullOrWhiteSpace(envelope.EntityId))
        {
            keys.Add($"seq:{envelope.EventSource}|{envelope.EventSequence}|{envelope.EntityId}");
        }

        return keys;
    }

    private static string BuildPayloadFingerprint(JsonObject payload)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        return Convert.ToHexString(bytes);
    }
}
