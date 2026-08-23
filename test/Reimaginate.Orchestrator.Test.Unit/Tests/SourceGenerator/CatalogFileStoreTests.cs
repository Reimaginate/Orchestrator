using FluentAssertions;
using Reimaginate.Orchestrator.SourceGenerator.CatalogTool;
using Xunit;

namespace Reimaginate.Orchestrator.Test.Unit.Tests.SourceGenerator;

[Collection(SourceGeneratorTestCollectionDefinition.CollectionName)]
public class CatalogFileStoreTests
{
    [Fact]
    public async Task WriteCatalogAsync_TouchesUnchangedContentWithoutRewriting()
    {
        using var tempDirectory = new TemporaryDirectory();
        var catalogPath = Path.Combine(tempDirectory.Path, "catalog.json");
        var fileStore = new CatalogFileStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        const string content = "{\"status\":\"success\"}";

        await fileStore.WriteCatalogAsync(catalogPath, content, cancellationToken);
        var initialWriteTime = File.GetLastWriteTimeUtc(catalogPath);

        await Task.Delay(75, cancellationToken);

        var result = await fileStore.WriteCatalogAsync(catalogPath, content, cancellationToken);

        result.WarningMessage.Should().BeNull();
        File.GetLastWriteTimeUtc(catalogPath).Should().BeAfter(initialWriteTime);
        (await File.ReadAllTextAsync(catalogPath, cancellationToken)).Should().Be(content);
    }

    [Fact]
    public async Task WriteCatalogAsync_ReturnsWarningWhenCatalogIsLocked()
    {
        using var tempDirectory = new TemporaryDirectory();
        var catalogPath = Path.Combine(tempDirectory.Path, "catalog.json");
        var fileStore = new CatalogFileStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(catalogPath, "{\"status\":\"old\"}", cancellationToken);

        CatalogWriteResult result;
        using (var lockStream = new FileStream(catalogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = await fileStore.WriteCatalogAsync(catalogPath, "{\"status\":\"new\"}", cancellationToken);
        }

        result.WarningCode.Should().Be(CatalogFileStore.WarningCode);
        result.WarningMessage.Should().Contain("locked by another process");
        (await File.ReadAllTextAsync(catalogPath, cancellationToken)).Should().Be("{\"status\":\"old\"}");
    }

    [Fact]
    public async Task DeleteCatalogIfExistsAsync_ReturnsWarningWhenCatalogIsLocked()
    {
        using var tempDirectory = new TemporaryDirectory();
        var catalogPath = Path.Combine(tempDirectory.Path, "catalog.json");
        var fileStore = new CatalogFileStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(catalogPath, "{\"status\":\"success\"}", cancellationToken);

        CatalogWriteResult result;
        using (var lockStream = new FileStream(catalogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = await fileStore.DeleteCatalogIfExistsAsync(catalogPath, cancellationToken);
        }

        result.WarningCode.Should().Be(CatalogFileStore.WarningCode);
        result.WarningMessage.Should().Contain("locked by another process");
        File.Exists(catalogPath).Should().BeTrue();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "orchestrator-catalog-store-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
