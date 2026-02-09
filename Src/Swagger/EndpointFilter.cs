using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace FastEndpoints.Swagger;

sealed class EndpointFilter : IOpenApiOperationTransformer
{
    readonly Func<EndpointDefinition, bool> _filter;

    public EndpointFilter(Func<EndpointDefinition, bool> filter)
    {
        _filter = filter;
    }

    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var def = context.Description
                         .ActionDescriptor
                         .EndpointMetadata
                         .OfType<EndpointDefinition>()
                         .SingleOrDefault();

        if (def is null)
            return Task.CompletedTask; //this is not a fast endpoint

        if (!_filter(def))
        {
            // Mark for removal
            operation.Description = "__REMOVE_FILTERED_ENDPOINT__";
        }

        return Task.CompletedTask;
    }
}