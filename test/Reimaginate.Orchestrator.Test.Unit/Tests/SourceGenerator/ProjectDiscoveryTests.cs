using FluentAssertions;
using Reimaginate.Orchestrator.SourceGenerator;
using Reimaginate.Orchestrator.Test.Unit.Base;
using Xunit;

namespace Reimaginate.Orchestrator.Test.Unit.Tests.SourceGenerator;

[Collection(SourceGeneratorTestCollectionDefinition.CollectionName)]
public class ProjectDiscoveryTests
{
    [Fact]
    public void FindRepoRoot_PrefersGitDirectory()
    {
        using var tempDirectory = new TemporaryDirectory("project-discovery");
        var repoRoot = tempDirectory.CreateSubdirectory("Repo");
        var nestedProjectDirectory = Directory.CreateDirectory(Path.Combine(repoRoot.FullName, "src", "Fixture.Host"));
        Directory.CreateDirectory(Path.Combine(repoRoot.FullName, ".git"));
        File.WriteAllText(Path.Combine(repoRoot.FullName, "Fixture.sln"), string.Empty);

        var resolvedRoot = ProjectDiscovery.FindRepoRoot(nestedProjectDirectory.FullName);

        resolvedRoot.Should().Be(repoRoot.FullName);
    }

    [Fact]
    public void FindRepoRoot_FallsBackToSolutionDirectory()
    {
        using var tempDirectory = new TemporaryDirectory("project-discovery");
        var solutionRoot = tempDirectory.CreateSubdirectory("Repo");
        var nestedProjectDirectory = Directory.CreateDirectory(Path.Combine(solutionRoot.FullName, "src", "Fixture.Host"));
        File.WriteAllText(Path.Combine(solutionRoot.FullName, "Fixture.sln"), string.Empty);

        var resolvedRoot = ProjectDiscovery.FindRepoRoot(nestedProjectDirectory.FullName);

        resolvedRoot.Should().Be(solutionRoot.FullName);
    }
}
