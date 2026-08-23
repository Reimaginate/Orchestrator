using System.Text.Json.Nodes;

namespace Reimaginate.Orchestrator.Common.Executors;

internal static class WorkflowErrorEnvelopeFactory
{
    public static JsonObject Create(Exception exception, string taskName)
    {
        if (exception is WorkflowRaisedException raised)
        {
            return Build(
                raised.ErrorType,
                string.IsNullOrWhiteSpace(raised.Message) ? exception.Message : raised.Message,
                raised.TaskName,
                raised.WorkflowData,
                "raise");
        }

        return Build(
            exception.GetType().Name,
            exception.Message,
            taskName,
            null,
            "runtime");
    }

    private static JsonObject Build(string type, string? message, string taskName, JsonObject? data, string source)
    {
        var payload = new JsonObject
        {
            ["type"] = type,
            ["message"] = message,
            ["task"] = taskName,
            ["source"] = source
        };

        payload["data"] = data?.DeepClone();
        return payload;
    }
}
