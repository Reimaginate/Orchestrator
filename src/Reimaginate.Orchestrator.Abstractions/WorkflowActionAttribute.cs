namespace Reimaginate.Orchestrator.Abstractions;

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class WorkflowActionAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}