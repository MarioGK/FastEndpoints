using System.Text.Json.Nodes;

namespace Swagger;

public class Fixture : AppFixture<Web.Program>
{
    public async Task<string> GenerateDocumentJsonAsync(string documentName)
    {
        var client = CreateClient();
        var response = await client.GetAsync($"/openapi/{Uri.EscapeDataString(documentName)}.json");
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            // Try to get detailed error from exception handler
            var detailUrl = $"/openapi/{Uri.EscapeDataString(documentName)}.json";
            throw new Exception($"Failed to get document '{documentName}' at '{detailUrl}': {response.StatusCode}\n{body}\nHeaders: {string.Join(", ", response.Headers.Select(h => $"{h.Key}={string.Join(",", h.Value)}"))}");
        }

        return body;
    }

    protected override ValueTask SetupAsync()
    {
        return ValueTask.CompletedTask;
    }
}