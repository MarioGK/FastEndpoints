using Microsoft.AspNetCore.OpenApi;

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
            if (!SchemaHelper.IsNullable(prop))
                schema.Required.Add(name);
        }

        return Task.CompletedTask;
    }
}