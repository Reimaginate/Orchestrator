using System.Diagnostics;

namespace Reimaginate.Orchestrator.Common;

public static class DiagnosticConfig
{
    public const string ServiceName = "Reimaginate.Saas.Orchestrator";
    public const string ApplicationName = "Orchestrator";
    public const string ApplicationVersion = "1.0.0";
    public static ActivitySource ActivitySource = new(ApplicationName, ApplicationVersion);
}