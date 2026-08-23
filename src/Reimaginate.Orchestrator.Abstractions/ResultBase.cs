namespace Reimaginate.Orchestrator.Abstractions;

public abstract class ResultBase {
    public abstract bool Success { get; set; }
    public abstract string? FailureReason { get; set; }
    public abstract object? UntypedData { get; set; }
}

public abstract class ResultWrapper<T> : ResultBase
{
    public abstract T? Data { get; set; }
}

public class Result<T> : ResultWrapper<T>
{
    public override bool Success { get; set; }
    public override string? FailureReason { get; set; }
    public override object? UntypedData { get; set; }

    public override T? Data
    {
        get => UntypedData is T typedData ? typedData : default;
        set => UntypedData = value;
    }
}

public class Result : Result<object?>
{
    public override bool Success { get; set; }
    public override string? FailureReason { get; set; }
    public override object? UntypedData { get; set; }

    public override object? Data
    {
        get => null;
        set { }
    }
}