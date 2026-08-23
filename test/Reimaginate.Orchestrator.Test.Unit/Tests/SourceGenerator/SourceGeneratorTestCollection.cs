using Xunit;

namespace Reimaginate.Orchestrator.Test.Unit.Tests.SourceGenerator;

[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class SourceGeneratorTestCollectionDefinition
{
    public const string CollectionName = "SourceGeneratorTests";
}
