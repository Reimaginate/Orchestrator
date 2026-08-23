namespace Reimaginate.Orchestrator.Common.Executors;

internal static class WorkflowTaskExecutionGuard
{
    public static async ValueTask<T> RunAsync<T>(string taskName, string locationHint, Func<ValueTask<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ShouldWrap(ex))
        {
            throw new WorkflowTaskExecutionException(taskName, locationHint, ex);
        }
    }

    public static async ValueTask RunAsync(string taskName, string locationHint, Func<ValueTask> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ShouldWrap(ex))
        {
            throw new WorkflowTaskExecutionException(taskName, locationHint, ex);
        }
    }

    private static bool ShouldWrap(Exception exception)
    {
        return exception is not WorkflowTaskExecutionException
            && exception is not OperationCanceledException;
    }
}
