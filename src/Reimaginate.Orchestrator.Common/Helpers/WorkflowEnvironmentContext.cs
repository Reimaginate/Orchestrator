using System.Text.Json.Nodes;

namespace Reimaginate.Orchestrator.Common.Helpers;

internal static class WorkflowEnvironmentContext
{
    public static JsonObject Enrich(JsonObject payload, JsonObject? environment)
    {
        var context = (JsonObject)payload.DeepClone();
        if (environment is not null)
        {
            context["env"] = environment.DeepClone();
        }

        return context;
    }

    public static JsonNode? Enrich(JsonNode? payload, JsonObject? environment)
    {
        if (payload is JsonObject obj)
        {
            return Enrich(obj, environment);
        }

        return payload?.DeepClone();
    }
}
