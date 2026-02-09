using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace FastEndpoints.Swagger;

sealed class FastEndpointsFilter : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var metaData = context.Description.ActionDescriptor.EndpointMetadata;

        if (!metaData.OfType<EndpointDefinition>().Any())
        {
            // Mark for removal
            operation.Description = "__REMOVE_NON_FASTENDPOINT__";
        }

        return Task.CompletedTask;
    }
}