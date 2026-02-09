using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;

namespace FastEndpoints.Swagger;

sealed class OperationSecurityTransformer(string schemeName) : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var epMeta = context.Description.ActionDescriptor.EndpointMetadata;
        var authNotRequired = epMeta.OfType<AllowAnonymousAttribute>().Any() || !epMeta.OfType<AuthorizeAttribute>().Any();
        var epDef = epMeta.OfType<EndpointDefinition>().SingleOrDefault();

        if (authNotRequired)
            return Task.CompletedTask;

        if (epDef is not null)
        {
            var epSchemes = epDef.AuthSchemeNames;

            if (epSchemes?.Contains(schemeName) == false)
                return Task.CompletedTask;
        }

        operation.Security ??= [];
        operation.Security.Add(
            new()
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new() { Type = ReferenceType.SecurityScheme, Id = schemeName }
                    },
                    BuildScopes(epMeta.OfType<AuthorizeAttribute>()).ToList()
                }
            });

        return Task.CompletedTask;
    }

    static IEnumerable<string> BuildScopes(IEnumerable<AuthorizeAttribute> authorizeAttributes)
    {
        return authorizeAttributes
               .Where(a => a.Roles != null)
               .SelectMany(a => a.Roles!.Split(','))
               .Distinct();
    }
}
