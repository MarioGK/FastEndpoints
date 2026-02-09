using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace FastEndpoints.Swagger;

sealed class MarkNonNullablePropsAsRequired : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (schema.Properties is null)
            return Task.CompletedTask;

        schema.Required ??= new HashSet<string>();

        foreach (var (name, prop) in schema.Properties)
        {
            if (prop is OpenApiSchema concreteSchema && concreteSchema.Type.HasValue && !concreteSchema.Type.Value.HasFlag(JsonSchemaType.Null))
                schema.Required.Add(name);
        }

        return Task.CompletedTask;
    }
}