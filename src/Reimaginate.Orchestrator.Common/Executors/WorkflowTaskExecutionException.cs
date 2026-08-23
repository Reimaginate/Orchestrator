namespace Reimaginate.Orchestrator.Common.Executors;

internal sealed class WorkflowTaskExecutionException(string taskName, string locationHint, Exception innerException)
    : Exception(innerException.Message, innerException)
{
    public string TaskName { get; } = taskName;

    public string LocationHint { get; } = locationHint;
}
