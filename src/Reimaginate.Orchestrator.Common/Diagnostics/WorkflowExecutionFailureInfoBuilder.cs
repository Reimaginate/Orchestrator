using System.Text.Json.Nodes;
using System.Reflection;
using FluentValidation;
using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.Common.Executors;

namespace Reimaginate.Orchestrator.Common.Diagnostics;

internal sealed record WorkflowExecutionFailureInfo(
    string FailureReason,
    string Message,
    string ExceptionType,
    WorkflowExecutionFailureDetails? FailureDetails)
{
    public JsonObject ToTracePayload()
    {
        var payload = new JsonObject
        {
            ["message"] = Message,
            ["exceptionType"] = ExceptionType,
            ["taskName"] = FailureDetails?.TaskName,
            ["locationHint"] = FailureDetails?.LocationHint,
            ["messages"] = new JsonArray((FailureDetails?.Messages ?? []).Select(message => (JsonNode?)JsonValue.Create(message)).ToArray())
        };

        return payload;
    }
}

internal static class WorkflowExecutionFailureInfoBuilder
{
    public static WorkflowExecutionFailureInfo Build(string baseMessage, Exception? exception)
    {
        var messages = FlattenMessages(exception, excludeWrapperNoise: true);
        if (messages.Count == 0)
        {
            messages = FlattenMessages(exception, excludeWrapperNoise: false);
        }

        var taskExecutionException = FindTaskExecutionException(exception);
        var meaningfulException = FindMeaningfulException(exception);
        var exceptionType = meaningfulException?.GetType().Name
            ?? taskExecutionException?.InnerException?.GetType().Name
            ?? exception?.GetType().Name
            ?? nameof(Exception);
        var detailMessages = messages.ToList();

        WorkflowExecutionFailureDetails? failureDetails = null;
        if (taskExecutionException is not null)
        {
            failureDetails = new WorkflowExecutionFailureDetails
            {
                TaskName = taskExecutionException.TaskName,
                LocationHint = taskExecutionException.LocationHint,
                ExceptionType = exceptionType,
                Messages = detailMessages
            };
        }

        return new WorkflowExecutionFailureInfo(
            BuildFailureReason(baseMessage, failureDetails, detailMessages),
            detailMessages.Count == 0 ? baseMessage : string.Join(" | ", detailMessages),
            exceptionType,
            failureDetails);
    }

    private static string BuildFailureReason(string baseMessage, WorkflowExecutionFailureDetails? failureDetails, IReadOnlyList<string> messages)
    {
        var parts = new List<string> { baseMessage };

        if (!string.IsNullOrWhiteSpace(failureDetails?.TaskName))
        {
            parts.Add($"Task '{failureDetails.TaskName}'.");
        }

        if (!string.IsNullOrWhiteSpace(failureDetails?.LocationHint))
        {
            parts.Add($"Location '{failureDetails.LocationHint}'.");
        }

        if (messages.Count > 0)
        {
            parts.Add(string.Join(" | ", messages));
        }

        return string.Join(" ", parts);
    }

    private static IReadOnlyList<string> FlattenMessages(Exception? exception, bool excludeWrapperNoise)
    {
        var messages = new List<string>();
        foreach (var current in Walk(exception))
        {
            foreach (var message in GetMessages(current, excludeWrapperNoise))
            {
                if (!messages.Contains(message, StringComparer.Ordinal))
                {
                    messages.Add(message);
                }
            }
        }

        return messages;
    }

    private static WorkflowTaskExecutionException? FindTaskExecutionException(Exception? exception)
    {
        return Walk(exception).OfType<WorkflowTaskExecutionException>().FirstOrDefault();
    }

    private static Exception? FindMeaningfulException(Exception? exception)
    {
        return Walk(exception).FirstOrDefault(current => !IsWrapperException(current));
    }

    private static IEnumerable<Exception> Walk(Exception? exception)
    {
        if (exception is null)
        {
            yield break;
        }

        var pending = new Queue<Exception>();
        pending.Enqueue(exception);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            yield return current;

            if (current is AggregateException aggregateException)
            {
                foreach (var inner in aggregateException.InnerExceptions)
                {
                    pending.Enqueue(inner);
                }
            }

            if (current.InnerException is not null)
            {
                pending.Enqueue(current.InnerException);
            }
        }
    }

    private static IEnumerable<string> GetMessages(Exception exception, bool excludeWrapperNoise)
    {
        if (exception is ValidationException validationException)
        {
            foreach (var message in validationException.Errors
                         .Select(error => error.ErrorMessage)
                         .Where(message => !string.IsNullOrWhiteSpace(message))
                         .Distinct(StringComparer.Ordinal))
            {
                yield return message;
            }

            yield break;
        }

        if (excludeWrapperNoise && IsWrapperMessage(exception.Message))
        {
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(exception.Message))
        {
            yield return exception.Message;
        }
    }

    private static bool IsWrapperException(Exception exception)
    {
        return exception is WorkflowTaskExecutionException
               || exception is TargetInvocationException
               || exception is AggregateException
               || IsWrapperMessage(exception.Message);
    }

    private static bool IsWrapperMessage(string? message)
    {
        return string.Equals(message, "Error occurred while processing request", StringComparison.Ordinal)
               || string.Equals(message, "Exception has been thrown by the target of an invocation.", StringComparison.Ordinal)
               || string.Equals(message, "One or more errors occurred.", StringComparison.Ordinal)
               || (!string.IsNullOrWhiteSpace(message)
                   && message.StartsWith("Error invoking handler for ", StringComparison.Ordinal));
    }
}
