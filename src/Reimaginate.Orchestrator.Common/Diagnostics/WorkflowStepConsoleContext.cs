using System.Globalization;

namespace Reimaginate.Orchestrator.Common.Diagnostics;

internal static class WorkflowStepConsoleContext
{
    private static readonly AsyncLocal<State?> CurrentState = new();

    public static IDisposable Begin(bool enabled, TextWriter? writer = null, TimeProvider? timeProvider = null)
    {
        if (!enabled)
        {
            return NoopScope.Instance;
        }

        var previous = CurrentState.Value;
        CurrentState.Value = new State(
            writer ?? Console.Out,
            timeProvider ?? TimeProvider.System);

        return new Scope(previous);
    }

    public static void Write(string stepName)
    {
        var state = CurrentState.Value;
        if (state is null || string.IsNullOrWhiteSpace(stepName))
        {
            return;
        }

        var timestamp = state.TimeProvider.GetUtcNow()
            .UtcDateTime
            .ToString("O", CultureInfo.InvariantCulture);
        var line = $"[{timestamp}] Step: {stepName}";

        lock (state.WriteLock)
        {
            state.Writer.WriteLine(line);
        }
    }

    private sealed record State(
        TextWriter Writer,
        TimeProvider TimeProvider)
    {
        public object WriteLock { get; } = new();
    }

    private sealed class Scope(State? previous) : IDisposable
    {
        private readonly State? _previous = previous;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            CurrentState.Value = _previous;
            _disposed = true;
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static NoopScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
