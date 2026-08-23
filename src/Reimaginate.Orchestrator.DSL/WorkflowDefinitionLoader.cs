namespace Reimaginate.Orchestrator.DSL;

public static class WorkflowDefinitionLoader
{
    public static Task<WorkflowAst> LoadAstAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        return WorkflowSyntaxParser.ParseAsync(stream, cancellationToken);
    }

    public static Task<WorkflowAst> LoadAstAsync(TextReader reader, CancellationToken cancellationToken = default)
    {
        return WorkflowSyntaxParser.ParseAsync(reader, cancellationToken);
    }

    public static async Task<WorkflowDefinition> LoadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var ast = await LoadAstAsync(stream, cancellationToken);
        return WorkflowAstBinder.Bind(ast);
    }

    public static async Task<WorkflowDefinition> LoadAsync(TextReader reader, CancellationToken cancellationToken = default)
    {
        var ast = await LoadAstAsync(reader, cancellationToken);
        return WorkflowAstBinder.Bind(ast);
    }
}
