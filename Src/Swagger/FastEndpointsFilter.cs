using Microsoft.AspNetCore.OpenApi;

namespace FastEndpoints.Swagger;

sealed class FastEndpointsFilter : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var metaData = context.Description.ActionDescriptor.EndpointMetadata;

        if (!metaData.OfType<EndpointDefinition>().Any())
        {
            // Mark for removal - we'll set a flag via extensions
            operation.Extensions ??= new Dictionary<string, Microsoft.OpenApi.Interfaces.IOpenApiExtension>();
            operation.Description = "__REMOVE_NON_FASTENDPOINT__";
        }

        return Task.CompletedTask;
    }
}