using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Workflows;
using Reimaginate.Orchestrator.Common.Executors;

namespace Reimaginate.Orchestrator.Common.Services.WorkflowDefinitions;

public sealed class LegacyReflectionWorkflowBuilderAdapter(ExecutorBinding start) : IWorkflowBuilderAdapter, IConditionalEdgeBuilder
{
    private readonly WorkflowBuilder _workflowBuilder = new(start);

    public bool SupportsRouteEdges => true;

    public IConditionalEdgeBuilder EdgeBuilder => this;

    public void AddUnconditionalEdge(ExecutorBinding from, ExecutorBinding to)
    {
        _workflowBuilder.AddEdge(from, to);
    }

    public void AddRouteEdge(ExecutorBinding from, ExecutorBinding to, string routeTaskName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeTaskName);

        if (TryAddConditionalEdgeOnWorkflowBuilder(from, to, routeTaskName))
        {
            return;
        }

        if (TryAddConditionalEdgeOnEdgeBuilder(from, to, routeTaskName))
        {
            return;
        }

        var builderMethods = string.Join(", ", _workflowBuilder.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public).Select(static method => method.Name).Distinct(StringComparer.Ordinal));
        throw new InvalidOperationException(
            $"Unable to materialize route-aware workflow edge from '{from.Id}' to '{to.Id}'. " +
            $"No supported conditional edge API was found on WorkflowBuilder. Available methods: {builderMethods}");
    }

    public void WithOutputFrom(params ExecutorBinding[] executors)
    {
        _workflowBuilder.WithOutputFrom(executors);
    }

    public Workflow Build()
    {
        return _workflowBuilder.Build(validateOrphans: false);
    }

    private bool TryAddConditionalEdgeOnWorkflowBuilder(ExecutorBinding from, ExecutorBinding to, string routeTaskName)
    {
        var addEdgeMethods = _workflowBuilder
            .GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.Name == "AddEdge");

        foreach (var method in addEdgeMethods)
        {
            var parameters = method.GetParameters();
            if (parameters.Length != 3)
            {
                continue;
            }

            if (!typeof(ExecutorBinding).IsAssignableFrom(parameters[0].ParameterType) ||
                !typeof(ExecutorBinding).IsAssignableFrom(parameters[1].ParameterType))
            {
                continue;
            }

            if (!TryBuildPredicateForMethodParameter(method, 2, routeTaskName, out var predicate, out var invocationMethod))
            {
                continue;
            }

            invocationMethod.Invoke(_workflowBuilder, [from, to, predicate]);
            return true;
        }

        return false;
    }

    private bool TryAddConditionalEdgeOnEdgeBuilder(ExecutorBinding from, ExecutorBinding to, string routeTaskName)
    {
        var edgeBuilder = _workflowBuilder.AddEdge(from, to);
        var conditionMethods = edgeBuilder
            .GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public);

        foreach (var method in conditionMethods)
        {
            var parameters = method.GetParameters();
            if (parameters.Length != 1)
            {
                continue;
            }

            if (!TryBuildPredicateForMethodParameter(method, 0, routeTaskName, out var predicate, out var invocationMethod))
            {
                continue;
            }

            invocationMethod.Invoke(edgeBuilder, [predicate]);
            return true;
        }

        return false;
    }

    private static bool TryBuildPredicateForMethodParameter(
        MethodInfo method,
        int parameterIndex,
        string routeTaskName,
        out Delegate predicate,
        out MethodInfo invocationMethod)
    {
        predicate = null!;
        invocationMethod = method;

        var parameters = method.GetParameters();
        if (parameterIndex < 0 || parameterIndex >= parameters.Length)
        {
            return false;
        }

        var predicateType = parameters[parameterIndex].ParameterType;

        if (predicateType.ContainsGenericParameters)
        {
            if (!method.IsGenericMethodDefinition || method.GetGenericArguments().Length != 1)
            {
                return false;
            }

            invocationMethod = method.MakeGenericMethod(typeof(JsonObject));
            predicateType = invocationMethod.GetParameters()[parameterIndex].ParameterType;
        }

        if (predicateType.GetMethod("Invoke")?.ReturnType != typeof(bool))
        {
            return false;
        }

        predicate = BuildRoutePredicate(predicateType, routeTaskName);
        return true;
    }

    private static Delegate BuildRoutePredicate(Type predicateType, string routeTaskName)
    {
        var invokeMethod = predicateType.GetMethod("Invoke")
            ?? throw new InvalidOperationException($"Unable to create workflow edge condition delegate for predicate type '{predicateType.FullName}'.");

        if (invokeMethod.ReturnType != typeof(bool))
        {
            throw new InvalidOperationException($"Workflow edge condition delegate '{predicateType.FullName}' does not return a boolean value.");
        }

        if (predicateType.ContainsGenericParameters || invokeMethod.GetParameters().Any(parameter => parameter.ParameterType.ContainsGenericParameters))
        {
            throw new InvalidOperationException(
                $"Workflow edge condition delegate '{predicateType.FullName}' contains open generic parameters and cannot be materialized.");
        }

        var predicateParameters = invokeMethod
            .GetParameters()
            .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name ?? "arg"))
            .ToArray();

        var matcherMethod = typeof(LegacyReflectionWorkflowBuilderAdapter)
            .GetMethod(nameof(ContainsSelectedRoute), BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not resolve selected route matcher helper method.");

        var routeNameExpression = Expression.Constant(routeTaskName, typeof(string));
        Expression matchesExpression = Expression.Constant(false, typeof(bool));

        foreach (var parameter in predicateParameters)
        {
            var checkExpression = Expression.Call(
                matcherMethod,
                Expression.Convert(parameter, typeof(object)),
                routeNameExpression);

            matchesExpression = Expression.OrElse(matchesExpression, checkExpression);
        }

        return Expression.Lambda(predicateType, matchesExpression, predicateParameters).Compile();
    }

    private static bool ContainsSelectedRoute(object input, string expectedRouteName)
    {
        if (TryGetSelectedRoute(input) is { Length: > 0 } routeName)
        {
            return string.Equals(routeName, expectedRouteName, StringComparison.Ordinal);
        }

        return false;
    }

    private static string? TryGetSelectedRoute(object? input)
    {
        if (input is null)
        {
            return null;
        }

        if (input is JsonObject jsonObject &&
            jsonObject[SwitchTaskExecutor.SelectedRouteProperty]?.GetValue<string>() is { Length: > 0 } directRouteName)
        {
            return directRouteName;
        }

        var inputType = input.GetType();
        foreach (var propertyName in new[] { "Input", "Current", "Payload", "Data", "Value" })
        {
            var property = inputType.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (property?.GetValue(input) is not { } nestedValue)
            {
                continue;
            }

            if (nestedValue is JsonObject nestedJsonObject &&
                nestedJsonObject[SwitchTaskExecutor.SelectedRouteProperty]?.GetValue<string>() is { Length: > 0 } nestedRouteName)
            {
                return nestedRouteName;
            }
        }

        return null;
    }
}
