using System.Text.Json;
using System.Text.Json.Nodes;

namespace FastEndpoints.Swagger;

/// <summary>
/// Helper to abstract differences between Microsoft.OpenApi v1 (net9.0) and v2 (net10.0+)
/// </summary>
internal static class SchemaHelper
{
#if NET10_0_OR_GREATER
    internal static void SetStringType(OpenApiSchema schema) => schema.Type = JsonSchemaType.String;
    internal static void SetIntegerType(OpenApiSchema schema, string format) { schema.Type = JsonSchemaType.Integer; schema.Format = format; }
    internal static void SetNumberType(OpenApiSchema schema, string format) { schema.Type = JsonSchemaType.Number; schema.Format = format; }
    internal static void SetBooleanType(OpenApiSchema schema) => schema.Type = JsonSchemaType.Boolean;
    internal static void SetObjectType(OpenApiSchema schema) => schema.Type = JsonSchemaType.Object;
    internal static void SetArrayType(OpenApiSchema schema) => schema.Type = JsonSchemaType.Array;

    internal static void MakeNullable(OpenApiSchema schema)
    {
        if (schema.Type.HasValue)
            schema.Type |= JsonSchemaType.Null;
    }

    internal static void RemoveNullable(OpenApiSchema schema)
    {
        if (schema.Type.HasValue && schema.Type.Value.HasFlag(JsonSchemaType.Null))
            schema.Type &= ~JsonSchemaType.Null;
    }

    internal static bool IsNullable(OpenApiSchema schema) => schema.Type?.HasFlag(JsonSchemaType.Null) == true;
    internal static bool IsStringType(OpenApiSchema schema) => schema.Type?.HasFlag(JsonSchemaType.String) == true;
    internal static bool IsArrayType(OpenApiSchema schema) => schema.Type?.HasFlag(JsonSchemaType.Array) == true;
    internal static bool IsObjectType(OpenApiSchema schema) => schema.Type?.HasFlag(JsonSchemaType.Object) == true;
    internal static bool IsIntegerType(OpenApiSchema schema) => schema.Type?.HasFlag(JsonSchemaType.Integer) == true;
    internal static bool IsNumberType(OpenApiSchema schema) => schema.Type?.HasFlag(JsonSchemaType.Number) == true;
    internal static bool IsBooleanType(OpenApiSchema schema) => schema.Type?.HasFlag(JsonSchemaType.Boolean) == true;

    internal static void SetEnumValues(OpenApiSchema schema, IList<JsonNode> values)
    {
        schema.Enum = values;
    }

    // In v2, Example is JsonNode? directly
    internal static void SetExample(OpenApiSchema schema, JsonNode? value) => schema.Example = value;
    internal static void SetExample(OpenApiParameter param, JsonNode? value) => param.Example = value;
    internal static void SetExample(OpenApiHeader header, JsonNode? value) => header.Example = value;
    internal static void SetExample(OpenApiMediaType mediaType, JsonNode? value) => mediaType.Example = value;
    internal static void SetExampleValue(OpenApiExample example, JsonNode? value) => example.Value = value;
    internal static JsonNode? GetExample(OpenApiSchema schema) => schema.Example;
    internal static JsonNode? GetExample(OpenApiParameter param) => param.Example;

    internal static void SetDefault(OpenApiSchema schema, JsonNode? value) => schema.Default = value;

    internal static bool GetRequired(OpenApiParameter param) => param.Required ?? false;
    internal static void SetRequired(OpenApiParameter param, bool value) => param.Required = value;
    internal static void SetRequired(OpenApiRequestBody body, bool value) => body.Required = value;
#else
    internal static void SetStringType(OpenApiSchema schema) => schema.Type = "string";
    internal static void SetIntegerType(OpenApiSchema schema, string format) { schema.Type = "integer"; schema.Format = format; }
    internal static void SetNumberType(OpenApiSchema schema, string format) { schema.Type = "number"; schema.Format = format; }
    internal static void SetBooleanType(OpenApiSchema schema) => schema.Type = "boolean";
    internal static void SetObjectType(OpenApiSchema schema) => schema.Type = "object";
    internal static void SetArrayType(OpenApiSchema schema) => schema.Type = "array";

    internal static void MakeNullable(OpenApiSchema schema) => schema.Nullable = true;
    internal static void RemoveNullable(OpenApiSchema schema) => schema.Nullable = false;

    internal static bool IsNullable(OpenApiSchema schema) => schema.Nullable;
    internal static bool IsStringType(OpenApiSchema schema) => schema.Type == "string";
    internal static bool IsArrayType(OpenApiSchema schema) => schema.Type == "array";
    internal static bool IsObjectType(OpenApiSchema schema) => schema.Type == "object";
    internal static bool IsIntegerType(OpenApiSchema schema) => schema.Type == "integer";
    internal static bool IsNumberType(OpenApiSchema schema) => schema.Type == "number";
    internal static bool IsBooleanType(OpenApiSchema schema) => schema.Type == "boolean";

    internal static void SetEnumValues(OpenApiSchema schema, IList<JsonNode> values)
    {
        schema.Enum = values.Select(v =>
        {
            var str = v?.GetValue<string>();
            return (Microsoft.OpenApi.Any.IOpenApiAny)new Microsoft.OpenApi.Any.OpenApiString(str ?? "");
        }).ToList();
    }

    // In v1, Example is IOpenApiAny - we convert JsonNode to/from IOpenApiAny
    internal static void SetExample(OpenApiSchema schema, JsonNode? value) => schema.Example = JsonNodeToOpenApiAny(value);
    internal static void SetExample(OpenApiParameter param, JsonNode? value) => param.Example = JsonNodeToOpenApiAny(value);
    internal static void SetExample(OpenApiHeader header, JsonNode? value) => header.Example = JsonNodeToOpenApiAny(value);
    internal static void SetExample(OpenApiMediaType mediaType, JsonNode? value) => mediaType.Example = JsonNodeToOpenApiAny(value);
    internal static void SetExampleValue(OpenApiExample example, JsonNode? value) => example.Value = JsonNodeToOpenApiAny(value);
    internal static JsonNode? GetExample(OpenApiSchema schema) => OpenApiAnyToJsonNode(schema.Example);
    internal static JsonNode? GetExample(OpenApiParameter param) => OpenApiAnyToJsonNode(param.Example);

    internal static void SetDefault(OpenApiSchema schema, JsonNode? value) => schema.Default = JsonNodeToOpenApiAny(value);

    internal static bool GetRequired(OpenApiParameter param) => param.Required;
    internal static void SetRequired(OpenApiParameter param, bool value) => param.Required = value;
    internal static void SetRequired(OpenApiRequestBody body, bool value) => body.Required = value;

    static Microsoft.OpenApi.Any.IOpenApiAny? JsonNodeToOpenApiAny(JsonNode? node)
    {
        if (node is null) return null;

        // Serialize to JSON string and parse as IOpenApiAny
        var json = node.ToJsonString();

        try
        {
            return new Microsoft.OpenApi.Any.OpenApiString(json, true);
        }
        catch
        {
            return new Microsoft.OpenApi.Any.OpenApiString(json);
        }
    }

    static JsonNode? OpenApiAnyToJsonNode(Microsoft.OpenApi.Any.IOpenApiAny? any)
    {
        if (any is null) return null;

        try
        {
            // Serialize the IOpenApiAny to JSON and parse back as JsonNode
            using var stream = new System.IO.MemoryStream();
            var writer = new Microsoft.OpenApi.Writers.OpenApiJsonWriter(new System.IO.StreamWriter(stream));
            any.Write(writer, Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_0);
            writer.Flush();
            stream.Position = 0;
            return JsonNode.Parse(stream);
        }
        catch
        {
            return null;
        }
    }
#endif
}
