using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;

namespace Swagger;

public class Fixture : AppFixture<Web.Program>
{
    public async Task<Microsoft.OpenApi.OpenApiDocument> GenerateDocumentAsync(string documentName)
    {
        var provider = Services.GetRequiredKeyedService<IOpenApiDocumentProvider>(documentName);
        return await provider.GetOpenApiDocumentAsync(CancellationToken.None);
    }

    protected override ValueTask SetupAsync()
    {
        return ValueTask.CompletedTask;
    }
}