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
#endif
}
