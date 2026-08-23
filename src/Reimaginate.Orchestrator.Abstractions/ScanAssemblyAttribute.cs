namespace Reimaginate.Orchestrator.Abstractions;

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class ScanAssemblyAttribute : Attribute
{
    public ScanAssemblyAttribute(Type assemblyMarker)
    {
        AssemblyMarker = assemblyMarker;
    }

    public ScanAssemblyAttribute(string assemblyName)
    {
        AssemblyName = assemblyName;
    }

    public Type? AssemblyMarker { get; }

    public string? AssemblyName { get; }
}
