using System.Reflection;
using Microsoft.Agents.AI.Workflows;
using Reimaginate.Orchestrator.Common.Diagnostics;

namespace Reimaginate.Orchestrator.Common.Helpers;

internal static class WorkflowRuntimeMetadataResolver
{
    private static readonly string[] WorkflowInstanceIdPropertyNames =
    [
        "WorkflowInstanceId",
        "RunId",
        "InstanceId"
    ];

    private static readonly string[] WorkflowTypePropertyNames =
    [
        "WorkflowType",
        "WorkflowName",
        "WorkflowId",
        "Name",
        "Type",
        "Id"
    ];

    private static readonly string[] WorkflowInfoPropertyNames =
    [
        "WorkflowInfo",
        "Workflow",
        "Run",
        "RunContext"
    ];

    private static readonly string[] WorkflowInstanceIdTraceKeys =
    [
        "workflowInstanceId",
        "runId",
        "instanceId",
        "workflow.instance.id",
        "run.id"
    ];

    private static readonly string[] WorkflowTypeTraceKeys =
    [
        "workflowType",
        "workflowName",
        "workflowId",
        "workflow.type",
        "workflow.name"
    ];

    internal sealed record Metadata(string? WorkflowType, string? WorkflowInstanceId);

    public static Metadata Resolve(IWorkflowContext? workflowContext)
    {
        var workflowInstanceId = ResolveWorkflowInstanceId(workflowContext);
        var workflowType = ResolveWorkflowType(workflowContext);

        var snapshot = WorkflowExecutionTraceContext.GetCurrentSnapshot();
        workflowInstanceId ??= snapshot?.WorkflowInstanceId;
        workflowType ??= snapshot?.WorkflowType;

        return new Metadata(workflowType, workflowInstanceId);
    }

    private static string? ResolveWorkflowInstanceId(IWorkflowContext? workflowContext)
    {
        if (workflowContext is null)
        {
            return null;
        }

        return ResolveFromTraceContext(workflowContext, WorkflowInstanceIdTraceKeys)
               ?? ResolveFromProperties(workflowContext, WorkflowInstanceIdPropertyNames)
               ?? ResolveFromNestedProperties(workflowContext, WorkflowInfoPropertyNames, WorkflowInstanceIdPropertyNames);
    }

    private static string? ResolveWorkflowType(IWorkflowContext? workflowContext)
    {
        if (workflowContext is null)
        {
            return null;
        }

        return ResolveFromTraceContext(workflowContext, WorkflowTypeTraceKeys)
               ?? ResolveFromProperties(workflowContext, WorkflowTypePropertyNames)
               ?? ResolveFromNestedProperties(workflowContext, WorkflowInfoPropertyNames, WorkflowTypePropertyNames);
    }

    private static string? ResolveFromTraceContext(IWorkflowContext workflowContext, IEnumerable<string> candidateKeys)
    {
        var traceContext = workflowContext.TraceContext;
        if (traceContext is null || traceContext.Count == 0)
        {
            return null;
        }

        foreach (var candidateKey in candidateKeys)
        {
            var match = traceContext.FirstOrDefault(entry => string.Equals(entry.Key, candidateKey, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match.Value))
            {
                return match.Value;
            }
        }

        return null;
    }

    private static string? ResolveFromProperties(object target, IEnumerable<string> candidatePropertyNames)
    {
        foreach (var propertyName in candidatePropertyNames)
        {
            if (TryGetReadablePropertyValue(target, propertyName) is string value
                && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? ResolveFromNestedProperties(object target, IEnumerable<string> candidateContainerNames, IEnumerable<string> candidatePropertyNames)
    {
        foreach (var containerName in candidateContainerNames)
        {
            var nested = TryGetReadablePropertyValue(target, containerName);
            if (nested is null)
            {
                continue;
            }

            var resolved = ResolveFromProperties(nested, candidatePropertyNames);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        return null;
    }

    private static object? TryGetReadablePropertyValue(object target, string propertyName)
    {
        try
        {
            var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (property is null || !property.CanRead || property.GetIndexParameters().Length != 0)
            {
                return null;
            }

            return property.GetValue(target);
        }
        catch
        {
            return null;
        }
    }
}
