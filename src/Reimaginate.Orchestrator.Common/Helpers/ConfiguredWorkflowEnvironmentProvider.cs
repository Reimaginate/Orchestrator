using System.Collections;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Reimaginate.Orchestrator.Common.Config;

namespace Reimaginate.Orchestrator.Common.Helpers;

internal sealed class ConfiguredWorkflowEnvironmentProvider(
    IConfiguration configuration,
    IOptions<OrchestratorOptions> options) : IWorkflowEnvironmentProvider
{
    public JsonObject BuildEnvironment()
        => BuildEnvironment(null);

    public JsonObject BuildEnvironment(string? workflowType)
    {
        var environmentOptions = options.Value.Environment;
        var environmentSection = ResolveEnvironmentSection();
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var globalAliasSources = GetAliasSourceKeys(environmentSection?.GetSection("Variables"));
        var workflowAliasSources = GetMatchingWorkflowAliasSourceKeys(environmentSection?.GetSection("Workflows"), workflowType);

        AddConfiguredValues(values, environmentOptions.Variables.Where(variable => variable is null || !globalAliasSources.Contains(variable)), environmentOptions.Prefixes);
        AddConfiguredValues(values, environmentSection?.GetSection("Variables"), environmentSection?.GetSection("Prefixes"));

        foreach (var workflowOptions in environmentOptions.Workflows.Where(scope => MatchesWorkflowType(workflowType, scope.WorkflowType)))
        {
            AddConfiguredValues(values, workflowOptions.Variables.Where(variable => variable is null || !workflowAliasSources.Contains(variable)), workflowOptions.Prefixes);
        }

        AddConfiguredWorkflowValues(values, environmentSection?.GetSection("Workflows"), workflowType);

        var result = new JsonObject();
        foreach (var (key, value) in values.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            result[key] = JsonValue.Create(value);
        }

        return result;
    }

    private void AddConfiguredValues(IDictionary<string, string?> values, IEnumerable<string?> variables, IEnumerable<string?> prefixes)
    {
        foreach (var variable in variables.Select(NormalizeKey).Where(static x => x is not null))
        {
            var value = ResolveValue(variable!);
            if (value is not null)
            {
                values[variable!] = value;
            }
        }

        var normalizedPrefixes = prefixes
            .Select(NormalizeKey)
            .Where(static x => x is not null)
            .Select(static x => x!)
            .ToArray();

        if (normalizedPrefixes.Length == 0)
        {
            return;
        }

        AddConfigurationPrefixMatches(values, normalizedPrefixes);
        AddProcessEnvironmentPrefixMatches(values, normalizedPrefixes);
    }

    private void AddConfiguredValues(IDictionary<string, string?> values, IConfigurationSection? variablesSection, IConfigurationSection? prefixesSection)
    {
        if (variablesSection is not null)
        {
            foreach (var child in variablesSection.GetChildren())
            {
                if (IsNumericKey(child.Key))
                {
                    AddExactValue(values, child.Value);
                }
                else
                {
                    AddAliasValue(values, child.Key, child.Value);
                }
            }
        }

        if (prefixesSection is null)
        {
            return;
        }

        var prefixes = prefixesSection
            .GetChildren()
            .Select(static child => child.Value)
            .Where(static value => value is not null);

        AddConfiguredValues(values, Array.Empty<string?>(), prefixes!);
    }

    private void AddConfiguredWorkflowValues(IDictionary<string, string?> values, IConfigurationSection? workflowsSection, string? workflowType)
    {
        if (workflowsSection is null)
        {
            return;
        }

        foreach (var workflowSection in workflowsSection.GetChildren().Where(section => MatchesWorkflowType(workflowType, GetWorkflowType(section))))
        {
            AddConfiguredValues(values, workflowSection.GetSection("Variables"), workflowSection.GetSection("Prefixes"));
        }
    }

    private void AddExactValue(IDictionary<string, string?> values, string? sourceKey)
    {
        var normalizedSourceKey = NormalizeKey(sourceKey);
        if (normalizedSourceKey is null)
        {
            return;
        }

        var value = ResolveValue(normalizedSourceKey);
        if (value is not null)
        {
            values[normalizedSourceKey] = value;
        }
    }

    private void AddAliasValue(IDictionary<string, string?> values, string? alias, string? sourceKey)
    {
        var normalizedAlias = NormalizeKey(alias);
        var normalizedSourceKey = NormalizeKey(sourceKey);
        if (normalizedAlias is null || normalizedSourceKey is null)
        {
            return;
        }

        var value = ResolveValue(normalizedSourceKey);
        if (value is not null)
        {
            values[normalizedAlias] = value;
        }
    }

    private string? ResolveValue(string key)
    {
        var value = configuration[key];
        if (value is not null)
        {
            return value;
        }

        const string rootSectionPrefix = OrchestratorOptions.DefaultSectionName + ":";
        if (key.StartsWith(rootSectionPrefix, StringComparison.Ordinal))
        {
            value = configuration[key[rootSectionPrefix.Length..]];
            if (value is not null)
            {
                return value;
            }
        }

        return System.Environment.GetEnvironmentVariable(key);
    }

    private void AddConfigurationPrefixMatches(IDictionary<string, string?> values, IReadOnlyCollection<string> prefixes)
    {
        foreach (var entry in configuration.AsEnumerable())
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || entry.Value is null || !MatchesAnyPrefix(entry.Key, prefixes))
            {
                continue;
            }

            values[entry.Key] = entry.Value;
        }
    }

    private static void AddProcessEnvironmentPrefixMatches(IDictionary<string, string?> values, IReadOnlyCollection<string> prefixes)
    {
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key || entry.Value is null || !MatchesAnyPrefix(key, prefixes))
            {
                continue;
            }

            values[key] = entry.Value.ToString();
        }
    }

    private static bool MatchesAnyPrefix(string key, IEnumerable<string> prefixes)
        => prefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal));

    private IConfigurationSection? ResolveEnvironmentSection()
    {
        var nestedSection = configuration.GetSection($"{OrchestratorOptions.DefaultSectionName}:Environment");
        if (SectionExists(nestedSection))
        {
            return nestedSection;
        }

        var directSection = configuration.GetSection("Environment");
        return SectionExists(directSection) ? directSection : null;
    }

    private static HashSet<string> GetAliasSourceKeys(IConfigurationSection? variablesSection)
    {
        var sourceKeys = new HashSet<string>(StringComparer.Ordinal);
        if (variablesSection is null)
        {
            return sourceKeys;
        }

        foreach (var child in variablesSection.GetChildren().Where(static child => !IsNumericKey(child.Key)))
        {
            var sourceKey = NormalizeKey(child.Value);
            if (sourceKey is not null)
            {
                sourceKeys.Add(sourceKey);
            }
        }

        return sourceKeys;
    }

    private static HashSet<string> GetMatchingWorkflowAliasSourceKeys(IConfigurationSection? workflowsSection, string? workflowType)
    {
        var sourceKeys = new HashSet<string>(StringComparer.Ordinal);
        if (workflowsSection is null)
        {
            return sourceKeys;
        }

        foreach (var workflowSection in workflowsSection.GetChildren().Where(section => MatchesWorkflowType(workflowType, GetWorkflowType(section))))
        {
            foreach (var sourceKey in GetAliasSourceKeys(workflowSection.GetSection("Variables")))
            {
                sourceKeys.Add(sourceKey);
            }
        }

        return sourceKeys;
    }

    private static string? GetWorkflowType(IConfigurationSection workflowSection)
        => NormalizeKey(workflowSection["WorkflowType"])
           ?? (IsNumericKey(workflowSection.Key)
               ? null
               : NormalizeKey(workflowSection.Key));

    private static bool SectionExists(IConfigurationSection section)
        => section.Value is not null || section.GetChildren().Any();

    private static bool IsNumericKey(string key)
        => int.TryParse(key, out _);

    private static string? NormalizeKey(string? key)
    {
        var normalized = key?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static bool MatchesWorkflowType(string? requestedWorkflowType, string? configuredWorkflowType)
    {
        var requested = NormalizeWorkflowType(requestedWorkflowType);
        var configured = NormalizeWorkflowType(configuredWorkflowType);
        return requested is not null
               && configured is not null
               && string.Equals(requested, configured, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeWorkflowType(string? workflowType)
    {
        var normalized = workflowType?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized.Replace('\\', '/');
    }
}
