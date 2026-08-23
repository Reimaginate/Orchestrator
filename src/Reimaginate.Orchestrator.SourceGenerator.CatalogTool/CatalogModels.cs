using System.Text.Json.Serialization;

namespace Reimaginate.Orchestrator.SourceGenerator.CatalogTool;

public sealed class WorkflowActionCatalog
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 2;

    [JsonPropertyName("generatedAtUtc")]
    public string GeneratedAtUtc { get; set; } = DateTime.UtcNow.ToString("O");

    [JsonPropertyName("repoRoot")]
    public string RepoRoot { get; set; } = string.Empty;

    [JsonPropertyName("hostProjectPath")]
    public string HostProjectPath { get; set; } = string.Empty;

    [JsonPropertyName("hostAssemblyName")]
    public string HostAssemblyName { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "error";

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("resolverTypes")]
    public IReadOnlyList<string> ResolverTypes { get; set; } = [];

    [JsonPropertyName("workflows")]
    public IReadOnlyList<WorkflowActionCatalogWorkflow> Workflows { get; set; } = [];
}

public sealed class WorkflowActionCatalogWorkflow
{
    [JsonPropertyName("projectPath")]
    public string ProjectPath { get; set; } = string.Empty;

    [JsonPropertyName("assemblyName")]
    public string AssemblyName { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "error";

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("actions")]
    public IReadOnlyList<WorkflowActionCatalogAction> Actions { get; set; } = [];
}

public sealed class WorkflowActionCatalogAction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("requestType")]
    public string RequestType { get; set; } = string.Empty;

    [JsonPropertyName("responseType")]
    public string? ResponseType { get; set; }

    [JsonPropertyName("requestProperties")]
    public IReadOnlyList<string> RequestProperties { get; set; } = [];

    [JsonPropertyName("responseProperties")]
    public IReadOnlyList<string> ResponseProperties { get; set; } = [];
}
