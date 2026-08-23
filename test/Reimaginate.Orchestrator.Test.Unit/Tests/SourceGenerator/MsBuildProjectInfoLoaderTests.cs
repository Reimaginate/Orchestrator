using FluentAssertions;
using Reimaginate.Orchestrator.SourceGenerator;
using Reimaginate.Orchestrator.Test.Unit.Base;
using Xunit;

namespace Reimaginate.Orchestrator.Test.Unit.Tests.SourceGenerator;

[Collection(SourceGeneratorTestCollectionDefinition.CollectionName)]
public class MsBuildProjectInfoLoaderTests
{
    [Fact]
    public async Task ReadPackageCompileReferences_ResolvesCompileAssembliesFromProjectAssets()
    {
        using var tempDirectory = new TemporaryDirectory("assets-refs");
        var packagesDirectory = tempDirectory.CreateSubdirectory("packages");
        var packageRoot = packagesDirectory.CreateSubdirectory("reimaginate.orchestrator.common");
        var packageVersionDirectory = packageRoot.CreateSubdirectory("1.1.1-rc.18");
        var libDirectory = packageVersionDirectory.CreateSubdirectory(Path.Combine("lib", "net10.0"));
        var assemblyPath = Path.Combine(libDirectory.FullName, "Reimaginate.Orchestrator.Common.dll");
        var objDirectory = tempDirectory.CreateSubdirectory("obj");
        var assetsFilePath = Path.Combine(objDirectory.FullName, "project.assets.json");

        await File.WriteAllTextAsync(assemblyPath, "placeholder", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            assetsFilePath,
            $$"""
            {
              "version": 3,
              "targets": {
                "net10.0": {
                  "Reimaginate.Orchestrator.Common/1.1.1-rc.18": {
                    "type": "package",
                    "compile": {
                      "lib/net10.0/Reimaginate.Orchestrator.Common.dll": {}
                    }
                  }
                }
              },
              "libraries": {
                "Reimaginate.Orchestrator.Common/1.1.1-rc.18": {
                  "type": "package",
                  "path": "reimaginate.orchestrator.common/1.1.1-rc.18"
                }
              },
              "packageFolders": {
                "{{packagesDirectory.FullName.Replace("\\", "\\\\")}}\\": {}
              }
            }
            """,
            TestContext.Current.CancellationToken);

        var references = MsBuildProjectInfoLoader.ReadPackageCompileReferences(assetsFilePath, "net10.0");

        references.Should().ContainSingle()
            .Which.Should().Be(assemblyPath);
    }
}
