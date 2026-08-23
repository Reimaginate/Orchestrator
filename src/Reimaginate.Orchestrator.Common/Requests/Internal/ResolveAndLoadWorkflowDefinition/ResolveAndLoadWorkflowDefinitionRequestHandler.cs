using Reimaginate.Mediator;
using Reimaginate.Orchestrator.Abstractions;
using Reimaginate.Orchestrator.DSL;

namespace Reimaginate.Orchestrator.Common.Requests.Internal.ResolveAndLoadWorkflowDefinition;

public class ResolveAndLoadWorkflowDefinitionRequestHandler(IWorkflowDefinitionStore workflowDefinitionStore) : IHandler<ResolveAndLoadWorkflowDefinitionRequest, ResolveAndLoadWorkflowDefinitionResponse>
{
    private readonly IWorkflowDefinitionStore _workflowDefinitionStore = workflowDefinitionStore ?? throw new ArgumentNullException(nameof(workflowDefinitionStore));

    public async Task<ResolveAndLoadWorkflowDefinitionResponse> HandleAsync(ResolveAndLoadWorkflowDefinitionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkflowType);

            var workflowStoreProvider = _workflowDefinitionStore.GetType().FullName ?? _workflowDefinitionStore.GetType().Name;
            var existsResponse = await _workflowDefinitionStore.ExistsAsync(request.WorkflowType, cancellationToken);
            if (!existsResponse.Success)
            {
                return new ResolveAndLoadWorkflowDefinitionResponse
                {
                    Success = false,
                    FailureReason = existsResponse.FailureReason ?? $"Failed to resolve workflow definition in provider '{workflowStoreProvider}'."
                };
            }

            if (!existsResponse.Data)
            {
                return new ResolveAndLoadWorkflowDefinitionResponse
                {
                    Success = false,
                    FailureReason = $"Workflow definition file was not found in workflow definition provider '{workflowStoreProvider}'."
                };
            }

            var definitionResponse = await _workflowDefinitionStore.OpenReadAsync(request.WorkflowType, cancellationToken);
            if (!definitionResponse.Success)
            {
                return new ResolveAndLoadWorkflowDefinitionResponse
                {
                    Success = false,
                    FailureReason = definitionResponse.FailureReason ?? $"Workflow definition file was not found in workflow definition provider '{workflowStoreProvider}'."
                };
            }

            var definitionYaml = definitionResponse.Data;
            if (string.IsNullOrWhiteSpace(definitionYaml))
            {
                return new ResolveAndLoadWorkflowDefinitionResponse
                {
                    Success = false,
                    FailureReason = $"Workflow definition '{request.WorkflowType}' returned empty content from provider '{workflowStoreProvider}'."
                };
            }

            using var definitionReader = new StringReader(definitionYaml);
            var ast = await WorkflowDefinitionLoader.LoadAstAsync(definitionReader, cancellationToken);
            var definition = WorkflowAstBinder.Bind(ast);

            return new ResolveAndLoadWorkflowDefinitionResponse
            {
                Success = true,
                Ast = ast,
                Definition = definition
            };
        }
        catch (Exception ex)
        {
            return new ResolveAndLoadWorkflowDefinitionResponse
            {
                Success = false,
                FailureReason = ex.Message
            };
        }
    }
}
