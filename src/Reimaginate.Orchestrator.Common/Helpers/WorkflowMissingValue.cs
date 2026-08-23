namespace Reimaginate.Orchestrator.Common.Helpers;

internal sealed record WorkflowMissingValue(string Path, string Expression)
{
    public WorkflowMissingValueException ToException()
        => new(Path, Expression);
}

internal static class WorkflowMissingValueHelper
{
    public static bool IsMissing(object? value)
        => value is WorkflowMissingValue;

    public static object? NormalizeMissingToNull(object? value)
        => value is WorkflowMissingValue ? null : value;

    public static object? RequirePresent(object? value)
        => value is WorkflowMissingValue missing ? throw missing.ToException() : value;
}

internal sealed class WorkflowMissingValueException(string path, string expression)
    : InvalidOperationException($"Missing JSON path '{path}' while evaluating expression '{expression}'.")
{
    public string Path { get; } = path;

    public string Expression { get; } = expression;
}
