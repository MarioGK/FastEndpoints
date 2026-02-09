using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;

namespace FastEndpoints.Swagger;

sealed class PolymorphismSchemaTransformer(DocumentOptions opts) : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (opts.UseOneOfForPolymorphism is false ||
            schema.Discriminator?.Mapping is null ||
            schema.Discriminator.Mapping.Count == 0 ||
            schema.OneOf is { Count: > 0 })
            return Task.CompletedTask;

        // Add derived schemas to oneOf
        schema.OneOf ??= [];
        foreach (var mapping in schema.Discriminator.Mapping)
        {
            schema.OneOf.Add(new OpenApiSchema
            {
                Reference = new() { Type = ReferenceType.Schema, Id = mapping.Value.TrimStart('#', '/', 'c', 'o', 'm', 'p', 'n', 'e', 't', 's', 'a', 'h') }
            });
        }

        if (schema.Discriminator.PropertyName is null || schema.Example is not null)
            return Task.CompletedTask;

        // Generate example with discriminator
        var example = new JsonObject
        {
            [schema.Discriminator.PropertyName] = schema.Discriminator.Mapping.FirstOrDefault().Key
        };

        schema.Example = example;

        return Task.CompletedTask;
    }
}