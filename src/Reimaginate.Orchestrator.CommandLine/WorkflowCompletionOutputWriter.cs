using System.Text.Json;

namespace Reimaginate.Orchestrator.CommandLine;

internal static class WorkflowCompletionOutputWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public static void WriteIfPresent(string workflowType, string workflowInstanceId, object? finalOutput)
    {
        if (finalOutput is null)
        {
            return;
        }

        var envelope = new
        {
            workflowType,
            workflowInstanceId,
            finalOutput
        };

        Console.WriteLine(JsonSerializer.Serialize(envelope, SerializerOptions));
    }
}
