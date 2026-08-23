using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using BuildTask = Microsoft.Build.Utilities.Task;

namespace Reimaginate.Orchestrator.SourceGenerator.CatalogTool;

public sealed class EmitWorkflowActionCatalogTask : BuildTask
{
    [Required]
    public string ProjectPath { get; set; } = string.Empty;

    [Required]
    public string CatalogOutputPath { get; set; } = string.Empty;

    public string? Configuration { get; set; }

    public string? TargetFramework { get; set; }

    public string? ProjectExtensionsPath { get; set; }

    public override bool Execute()
    {
        try
        {
            var writer = new WorkflowActionCatalogWriter(new MsBuildProjectLoadOptions
            {
                ProjectPath = ProjectPath,
                Configuration = Configuration,
                TargetFramework = TargetFramework,
                ProjectExtensionsPath = ProjectExtensionsPath
            });
            var result = writer.WriteAsync(ProjectPath, CatalogOutputPath, CancellationToken.None).GetAwaiter().GetResult();

            if (!string.IsNullOrWhiteSpace(result.WarningMessage))
            {
                Log.LogWarning(
                    subcategory: string.Empty,
                    warningCode: result.WarningCode,
                    helpKeyword: null,
                    file: ProjectPath,
                    lineNumber: 0,
                    columnNumber: 0,
                    endLineNumber: 0,
                    endColumnNumber: 0,
                    message: result.WarningMessage);
            }

            return !Log.HasLoggedErrors;
        }
        catch (Exception ex)
        {
            Log.LogErrorFromException(ex, showStackTrace: true);
            return false;
        }
    }
}
