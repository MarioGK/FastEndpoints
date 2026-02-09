using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace FastEndpoints.Swagger;

/// <summary>
/// a set of extension methods for adding swagger support
/// </summary>
public static class Extensions
{
    /// <summary>
    /// JsonNamingPolicy chosen for swagger
    /// </summary>
    public static JsonNamingPolicy? SelectedJsonNamingPolicy { get; private set; }

    static int _docIndex;

    /// <summary>
    /// enable support for FastEndpoints and create a swagger document.
    /// </summary>
    /// <param name="options">swagger document configuration options</param>
    public static IServiceCollection SwaggerDocument(this IServiceCollection services, Action<DocumentOptions>? options = null)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
            return services;

        services.AddEndpointsApiExplorer();

        var docName = $"v{Interlocked.Increment(ref _docIndex)}";

        // Pre-resolve document name from user options
        var tempDocForName = new DocumentOptions(null!);
        options?.Invoke(tempDocForName);
        if (tempDocForName.DocumentName is not null)
            docName = tempDocForName.DocumentName;

        // We need to capture the document options configuration for later use by transformers
        // Since AddOpenApi doesn't provide IServiceProvider, we store config and let transformers resolve services
        var docConfig = new DocumentOptionsConfig { ConfigureAction = options };

        services.AddKeyedSingleton(docName, docConfig);

        services.AddOpenApi(
            docName,
            openApiOptions =>
            {
                // Apply user document settings
                var tempDoc = new DocumentOptions(null!);
                options?.Invoke(tempDoc);

                var stjOpts = new JsonSerializerOptions(Cfg.SerOpts.Options);
                SelectedJsonNamingPolicy = stjOpts.PropertyNamingPolicy;
                tempDoc.SerializerSettings?.Invoke(stjOpts);

                if (tempDoc.EnableJWTBearerAuth)
                    openApiOptions.EnableJWTBearerAuth();

                if (tempDoc.EndpointFilter is not null)
                    openApiOptions.AddOperationTransformer(new EndpointFilter(tempDoc.EndpointFilter));

                if (tempDoc.ExcludeNonFastEndpoints)
                    openApiOptions.AddOperationTransformer(new FastEndpointsFilter());

                if (tempDoc.TagDescriptions is not null)
                {
                    var dict = new Dictionary<string, string>();
                    tempDoc.TagDescriptions(dict);
                    openApiOptions.AddDocumentTransformer(
                        (doc, _, _) =>
                        {
                            doc.Tags ??= new HashSet<OpenApiTag>();
                            foreach (var kvp in dict)
                            {
                                doc.Tags.Add(
                                    new()
                                    {
                                        Name = kvp.Key,
                                        Description = kvp.Value
                                    });
                            }

                            return Task.CompletedTask;
                        });
                }

                // Register FastEndpoints transformers
                openApiOptions.AddSchemaTransformer(new ValidationSchemaTransformer());
                openApiOptions.AddSchemaTransformer(new PolymorphismSchemaTransformer(tempDoc));
                openApiOptions.AddOperationTransformer(new OperationTransformer(tempDoc, stjOpts));
                openApiOptions.AddDocumentTransformer(
                    new DocumentTransformer(
                        tempDoc.MinEndpointVersion,
                        tempDoc.MaxEndpointVersion,
                        tempDoc.ReleaseVersion,
                        tempDoc.ShowDeprecatedOps));

                if (tempDoc.RemoveEmptyRequestSchema || tempDoc.FlattenSchema)
                {
                    openApiOptions.AddSchemaTransformer(new FlattenSchemaTransformer());
                }

                // Set document title and version via document transformer
                if (tempDoc.Title is not null || tempDoc.Version is not null)
                {
                    var title = tempDoc.Title;
                    var version = tempDoc.Version;
                    openApiOptions.AddDocumentTransformer(
                        (doc, _, _) =>
                        {
                            doc.Info ??= new OpenApiInfo();
                            if (title is not null)
                                doc.Info.Title = title;
                            if (version is not null)
                                doc.Info.Version = version;

                            return Task.CompletedTask;
                        });
                }

                tempDoc.DocumentSettings?.Invoke(openApiOptions);
            });

        return services;
    }

    /// <summary>
    /// enables the open-api/swagger middleware for fastendpoints.
    /// this method maps the OpenApi document endpoint and the Scalar API reference UI.
    /// </summary>
    /// <param name="openApiConfig">optional config action for the open-api endpoint</param>
    /// <param name="scalarConfig">optional config action for the Scalar API reference UI</param>
    public static WebApplication UseSwaggerGen(this WebApplication app,
                                               Action<OpenApiOptions>? openApiConfig = null,
                                               Action<ScalarOptions>? scalarConfig = null)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
            throw new NotSupportedException("Not supported in AOT applications! Use Scalar for API visualization.");

        app.MapOpenApi();
        app.MapScalarApiReference(
            o =>
            {
                ConfigureScalarDefaults(o);
                scalarConfig?.Invoke(o);
            });

        return app;
    }

    /// <summary>
    /// enable support for FastEndpoints in swagger
    /// </summary>
    /// <param name="documentOptions">the document options</param>
    /// <param name="serviceProvider">the service provider</param>
    public static void EnableFastEndpoints(this OpenApiOptions settings,
                                           Action<DocumentOptions> documentOptions,
                                           IServiceProvider serviceProvider)
    {
        var doc = new DocumentOptions(serviceProvider);
        documentOptions(doc);

        var stjOpts = new JsonSerializerOptions(Cfg.SerOpts.Options);
        doc.SerializerSettings?.Invoke(stjOpts);

        settings.AddSchemaTransformer(new ValidationSchemaTransformer());
        settings.AddSchemaTransformer(new PolymorphismSchemaTransformer(doc));
        settings.AddOperationTransformer(new OperationTransformer(doc, stjOpts));
        settings.AddDocumentTransformer(
            new DocumentTransformer(
                doc.MinEndpointVersion,
                doc.MaxEndpointVersion,
                doc.ReleaseVersion,
                doc.ShowDeprecatedOps));
    }

    /// <summary>
    /// enable jwt bearer authorization support
    /// </summary>
    public static void EnableJWTBearerAuth(this OpenApiOptions settings)
    {
        settings.AddAuth(
            "JWTBearerAuth",
            new()
            {
                Type = SecuritySchemeType.Http,
                Scheme = "Bearer",
                BearerFormat = "JWT",
                Description = "Enter a JWT token to authorize the requests..."
            });
    }

    /// <summary>
    /// configure Scalar API Reference UI with some sensible defaults for FastEndpoints which can be overridden if needed.
    /// </summary>
    /// <param name="settings">provide an action that overrides any of the defaults</param>
    public static void ConfigureScalarDefaults(ScalarOptions o, Action<ScalarOptions>? settings = null)
    {
        o.DarkMode = true;
        o.ShowSidebar = true;
        settings?.Invoke(o);
    }

    /// <summary>
    /// add swagger auth for this open api document
    /// </summary>
    /// <param name="schemeName">the authentication scheme</param>
    /// <param name="securityScheme">an open api security scheme object</param>
    /// <param name="globalScopeNames">a collection of global scope names</param>
    public static OpenApiOptions AddAuth(this OpenApiOptions s,
                                         string schemeName,
                                         OpenApiSecurityScheme securityScheme,
                                         IEnumerable<string>? globalScopeNames = null)
    {
        s.AddDocumentTransformer(
            (doc, _, _) =>
            {
                doc.Components ??= new();
                doc.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                doc.Components.SecuritySchemes[schemeName] = securityScheme;
                return Task.CompletedTask;
            });

        s.AddOperationTransformer(new OperationSecurityTransformer(schemeName));

        return s;
    }

    /// <summary>
    /// mark all non-nullable properties of the schema as required in the swagger document.
    /// this may only be needed for TS client generation with OAS3 swagger definitions.
    /// </summary>
    public static void MarkNonNullablePropsAsRequired(this OpenApiOptions x)
        => x.AddSchemaTransformer(new MarkNonNullablePropsAsRequired());

    /// <summary>
    /// gets the <see cref="EndpointDefinition" /> from the operation transformer context if this is a FastEndpoint operation. otherwise returns null.
    /// </summary>
    public static EndpointDefinition? GetEndpointDefinition(this OpenApiOperationTransformerContext ctx)
        => ctx.Description
              .ActionDescriptor
              .EndpointMetadata
              .OfType<EndpointDefinition>()
              .SingleOrDefault();

    /// <summary>
    /// gets the example object if any, from a given <see cref="DefaultProducesResponseMetadata" /> internal class
    /// </summary>
    public static object? GetExampleFromMetaData(this IProducesResponseTypeMetadata metadata)
        => (metadata as DefaultProducesResponseMetadata)?.Example;

    /// <summary>
    /// when path based auto-tagging is enabled, you can use this method to specify an override tag name if necessary.
    /// </summary>
    /// <param name="tag">the tag name to use instead of the auto tag</param>
    public static IEndpointConventionBuilder AutoTagOverride(this IEndpointConventionBuilder b, string tag)
    {
        b.WithMetadata(new AutoTagOverride(tag));

        return b;
    }

    /// <summary>
    /// disable swagger+fluentvalidation integration for a property rule
    /// </summary>
    public static IRuleBuilderOptions<T, TProperty> SwaggerIgnore<T, TProperty>(this IRuleBuilderOptions<T, TProperty> rule,
                                                                                ApplyConditionTo applyConditionTo = ApplyConditionTo.AllValidators)
    {
        return rule.When(_ => true, applyConditionTo);
    }

    internal static string Remove(this string value, string removeString)
    {
        var index = value.IndexOf(removeString, StringComparison.Ordinal);

        return index < 0 ? value : value.Remove(index, removeString.Length);
    }

    internal static bool HasNoProperties(this IDictionary<string, OpenApiMediaType> content)
        => !content.Any(c => c.Value.Schema?.Properties?.Count > 0);

    internal static IEnumerable<KeyValuePair<string, IOpenApiSchema>> GetAllProperties(this KeyValuePair<string, OpenApiMediaType> mediaType)
    {
        var schema = mediaType.Value.Schema;

        if (schema is not OpenApiSchema concreteSchema)
            return [];

        return GetSchemaProperties(concreteSchema);
    }

    internal static IEnumerable<KeyValuePair<string, IOpenApiSchema>> GetAllProperties(this KeyValuePair<string, IOpenApiResponse> response)
    {
        var firstContent = response.Value.Content?.FirstOrDefault().Value;

        if (firstContent?.Schema is not OpenApiSchema concreteSchema)
            return [];

        return GetSchemaProperties(concreteSchema);
    }

    static IEnumerable<KeyValuePair<string, IOpenApiSchema>> GetSchemaProperties(OpenApiSchema schema)
    {
        var properties = schema.Properties ?? new Dictionary<string, IOpenApiSchema>();

        // Also include properties from AllOf schemas (inherited properties)
        if (schema.AllOf is { Count: > 0 })
        {
            foreach (var allOfSchema in schema.AllOf)
            {
                if (allOfSchema.Properties is { Count: > 0 })
                {
                    properties = properties.Concat(allOfSchema.Properties)
                                           .DistinctBy(p => p.Key)
                                           .ToDictionary(p => p.Key, p => p.Value);
                }
            }
        }

        return properties;
    }

    internal static object? GetParentCtorDefaultValue(this PropertyInfo p)
    {
        var tParent = p.DeclaringType;

        if (tParent?.IsClass is not true)
            return null;

        return tParent.GetConstructors()
                      .Select(c => c.GetParameters())
                      .MaxBy(pi => pi.Length)?
                      .SingleOrDefault(
                          pi => pi.HasDefaultValue &&
                                pi.Name?.Equals(p.Name, StringComparison.OrdinalIgnoreCase) is true)?.DefaultValue;
    }

    static readonly ConcurrentDictionary<PropertyInfo, NullabilityInfo> _nullInfoCache = new();

    internal static bool IsNullable(this PropertyInfo prop)
    {
        return _nullInfoCache.GetOrAdd(prop, pi => new NullabilityInfoContext().Create(pi))
                             .WriteState is NullabilityState.Nullable;
    }

    internal static string? GetXmlExample(this PropertyInfo p)
    {
        var example = p.GetXmlDocsTag("example");

        return string.IsNullOrEmpty(example) ? null : example;
    }

    internal static JsonNode? GetExampleJsonNode(this PropertyInfo? p, JsonSerializerOptions serializerOptions)
    {
        var exampleStr = p?.GetXmlExample();

        if (exampleStr is null)
            return null;

        if (!exampleStr.IsJsonObjectString() && !exampleStr.IsJsonArrayString())
            return JsonValue.Create(exampleStr);

        try
        {
            var deserialized = JsonSerializer.Deserialize<JsonNode>(exampleStr, serializerOptions);
            return deserialized;
        }
        catch
        {
            return null;
        }
    }

    static bool IsJsonArrayString(this string? val)
        => val?.Length > 1 && val[0] == '[' && val[^1] == ']';

    static bool IsJsonObjectString(this string? val)
        => val?.Length > 1 && val[0] == '{' && val[^1] == '}';

    internal static string? GetSummary(this Type p)
    {
        var summary = p.GetXmlDocsSummary();

        return string.IsNullOrEmpty(summary) ? null : summary;
    }

    internal static string? GetDescription(this Type p)
    {
        var remarks = p.GetXmlDocsRemarks();

        return string.IsNullOrEmpty(remarks) ? null : remarks;
    }

    internal static string ApplyPropNamingPolicy(this string paramName, DocumentOptions documentOptions)
        => documentOptions.UsePropertyNamingPolicy && SelectedJsonNamingPolicy is not null
               ? SelectedJsonNamingPolicy.ConvertName(paramName)
               : paramName;

    internal static IEnumerable<KeyValuePair<string, IOpenApiSchema>> GetAllRequestProperties(this KeyValuePair<string, OpenApiMediaType> mediaType)
    {
        if (mediaType.Value.Schema is not OpenApiSchema rootSchema)
            return [];

        var allProperties = (rootSchema.Properties ?? new Dictionary<string, IOpenApiSchema>()).ToList();

        if (rootSchema.AllOf is { Count: > 0 })
        {
            foreach (var allOfSchema in rootSchema.AllOf)
            {
                if (allOfSchema.Properties is { Count: > 0 })
                    allProperties.AddRange(allOfSchema.Properties);
            }
        }

        var res = new List<KeyValuePair<string, IOpenApiSchema>>();
        var visitedSchemas = new HashSet<IOpenApiSchema>();
        const int maxDepth = 100;

        TraverseProperties(string.Empty, allProperties.DistinctBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value), res, visitedSchemas, 0, maxDepth);

        return res;

        static void TraverseProperties(string parentPath,
                                       IDictionary<string, IOpenApiSchema> props,
                                       List<KeyValuePair<string, IOpenApiSchema>> result,
                                       HashSet<IOpenApiSchema> visitedSchemas,
                                       int currentDepth,
                                       int maxDepth)
        {
            if (currentDepth > maxDepth)
                return;

            foreach (var prop in props)
            {
                var currentPath = string.IsNullOrEmpty(parentPath)
                                      ? prop.Key
                                      : $"{parentPath}.{prop.Key}";

                result.Add(new(currentPath, prop.Value));

                if (!visitedSchemas.Add(prop.Value))
                    continue;

                if (prop.Value.Properties is { Count: > 0 })
                    TraverseProperties(currentPath, prop.Value.Properties, result, visitedSchemas, currentDepth + 1, maxDepth);

                if (prop.Value is not OpenApiSchema concreteSchema || !IsCollectionType(concreteSchema))
                    continue;

                var itemSchema = concreteSchema.Items;

                if (itemSchema?.Properties is not { Count: > 0 } || visitedSchemas.Contains(itemSchema))
                    continue;

                var collectionPath = $"{currentPath}[0]";
                visitedSchemas.Add(itemSchema);
                TraverseProperties(collectionPath, itemSchema.Properties, result, visitedSchemas, currentDepth + 1, maxDepth);
            }
        }

        static bool IsCollectionType(OpenApiSchema property)
        {
            return property.Type?.HasFlag(JsonSchemaType.Array) == true ||
                   (property.Type?.HasFlag(JsonSchemaType.Object) == true &&
                    property.AllOf is { Count: > 0 } &&
                    property.AllOf.Any(s => s is OpenApiSchema cs && cs.Type?.HasFlag(JsonSchemaType.Array) == true));
        }
    }

    /// <summary>
    /// exports swagger.json files to disk (ONLY DURING NATIVE AOT PUBLISHING) and exits the program.
    /// <para>HINT: make sure to place the call straight after <c>app.UseFastEndpoints()</c></para>
    /// <para>
    /// to enable automatic export during AOT publish builds, add this to your .csproj:
    /// <code>
    /// &lt;PropertyGroup&gt;
    ///     &lt;ExportSwaggerDocs&gt;true&lt;/ExportSwaggerDocs&gt;
    /// &lt;/PropertyGroup&gt;
    /// </code>
    /// </para>
    /// <para>
    /// to customize the export path, add this to your .csproj:
    /// <code>
    /// &lt;PropertyGroup&gt;
    ///     &lt;SwaggerExportPath&gt;wwwroot/swagger&lt;/SwaggerExportPath&gt;
    /// &lt;/PropertyGroup&gt;
    /// </code>
    /// </para>
    /// <para>
    /// to force generate swagger docs outside a AOT publish, run the following in a terminal:
    /// <code>dotnet run --export-swagger-docs true -p:PublishAot=false</code>
    /// optionally specify the output folder:
    /// <code>dotnet run --export-swagger-docs true -p:PublishAot=false -p:SwaggerExportPath=wwwroot/swagger</code>
    /// </para>
    /// </summary>
    /// <param name="documentNames">the swagger document names to export. these must match the names used in <c>.SwaggerDocument()</c> configuration.</param>
    public static async Task ExportSwaggerDocsAndExitAsync(this WebApplication app, params string[] documentNames)
    {
        if (app.Configuration["export-swagger-docs"] != "true")
            return;

        if (documentNames.Length == 0)
            return;

        var destinationPath = Path.Combine(app.Environment.ContentRootPath, DocumentOptions.SwaggerExportPath);

        await app.StartAsync();

        var logger = app.Services.GetRequiredService<ILogger<SwaggerExportRunner>>();

        Directory.CreateDirectory(destinationPath);

        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.FirstOrDefault() ?? "http://localhost:5000") };

        foreach (var docName in documentNames)
        {
            try
            {
                logger.ExportingSwaggerDoc(docName);
                var json = await client.GetStringAsync($"/openapi/{docName}.json");
                var filePath = Path.Combine(destinationPath, $"{docName}.json");
                await File.WriteAllTextAsync(filePath, json);
                logger.SwaggerDocExportSuccessful(docName, filePath);
            }
            catch (Exception ex)
            {
                logger.SwaggerDocExportFailed(docName, ex.Message);
            }
        }

        await app.StopAsync();
        Environment.Exit(0);
    }

    internal static TValue GetOrAdd<TKey, TValue>(this IDictionary<TKey, TValue> dict, TKey key, TValue value)
    {
        ArgumentNullException.ThrowIfNull(dict);

        if (dict.TryGetValue(key, out var existing))
            return existing;

        dict[key] = value;

        return value;
    }

    /// <summary>
    /// creates an OpenApiSchema for a given .NET type
    /// </summary>
    internal static OpenApiSchema CreateSchemaForType(Type type)
    {
        var underlyingType = Nullable.GetUnderlyingType(type) ?? type;
        var isNullable = Nullable.GetUnderlyingType(type) is not null;

        var schema = new OpenApiSchema();

        if (underlyingType == typeof(string))
            schema.Type = JsonSchemaType.String;
        else if (underlyingType == typeof(bool))
            schema.Type = JsonSchemaType.Boolean;
        else if (underlyingType == typeof(int) || underlyingType == typeof(short) || underlyingType == typeof(byte))
        {
            schema.Type = JsonSchemaType.Integer;
            schema.Format = "int32";
        }
        else if (underlyingType == typeof(long))
        {
            schema.Type = JsonSchemaType.Integer;
            schema.Format = "int64";
        }
        else if (underlyingType == typeof(float))
        {
            schema.Type = JsonSchemaType.Number;
            schema.Format = "float";
        }
        else if (underlyingType == typeof(double))
        {
            schema.Type = JsonSchemaType.Number;
            schema.Format = "double";
        }
        else if (underlyingType == typeof(decimal))
        {
            schema.Type = JsonSchemaType.Number;
            schema.Format = "decimal";
        }
        else if (underlyingType == typeof(DateTime) || underlyingType == typeof(DateTimeOffset))
        {
            schema.Type = JsonSchemaType.String;
            schema.Format = "date-time";
        }
        else if (underlyingType == typeof(DateOnly))
        {
            schema.Type = JsonSchemaType.String;
            schema.Format = "date";
        }
        else if (underlyingType == typeof(TimeOnly) || underlyingType == typeof(TimeSpan))
        {
            schema.Type = JsonSchemaType.String;
            schema.Format = "time";
        }
        else if (underlyingType == typeof(Guid))
        {
            schema.Type = JsonSchemaType.String;
            schema.Format = "uuid";
        }
        else if (underlyingType == typeof(Uri))
        {
            schema.Type = JsonSchemaType.String;
            schema.Format = "uri";
        }
        else if (underlyingType == typeof(byte[]))
        {
            schema.Type = JsonSchemaType.String;
            schema.Format = "binary";
        }
        else if (underlyingType.IsEnum)
        {
            schema.Type = JsonSchemaType.String;
            schema.Enum = underlyingType.GetEnumNames().Select(n => (JsonNode)JsonValue.Create(n)!).ToList();
        }
        else
            schema.Type = JsonSchemaType.String;

        if (isNullable)
            schema.Type |= JsonSchemaType.Null;

        return schema;
    }

    /// <summary>
    /// generates a sample JSON node from the schema for use as an example
    /// </summary>
    internal static JsonNode? GenerateSampleJson(this OpenApiSchema schema)
    {
        if (schema.Type?.HasFlag(JsonSchemaType.Object) == true || schema.Properties is { Count: > 0 })
        {
            var obj = new JsonObject();

            if (schema.Properties is not null)
            {
                foreach (var prop in schema.Properties)
                {
                    if (prop.Value is OpenApiSchema propSchema)
                        obj[prop.Key] = GenerateSampleJson(propSchema);
                }
            }

            return obj;
        }

        if (schema.Type?.HasFlag(JsonSchemaType.Array) == true && schema.Items is OpenApiSchema itemSchema)
            return new JsonArray(GenerateSampleJson(itemSchema));

        if (schema.Type?.HasFlag(JsonSchemaType.String) == true)
            return JsonValue.Create("string");
        if (schema.Type?.HasFlag(JsonSchemaType.Integer) == true)
            return JsonValue.Create(0);
        if (schema.Type?.HasFlag(JsonSchemaType.Number) == true)
            return JsonValue.Create(0.0);
        if (schema.Type?.HasFlag(JsonSchemaType.Boolean) == true)
            return JsonValue.Create(false);

        return null;
    }
}

/// <summary>
/// internal config holder for document options
/// </summary>
internal sealed class DocumentOptionsConfig
{
    public Action<DocumentOptions>? ConfigureAction { get; init; }
}

/// <summary>
/// a no-op schema transformer for flattening schema inheritance
/// </summary>
internal sealed class FlattenSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        // Flatten AllOf into direct properties
        if (schema.AllOf is { Count: > 0 })
        {
            schema.Properties ??= new Dictionary<string, IOpenApiSchema>();
            schema.Required ??= new HashSet<string>();

            foreach (var allOfSchema in schema.AllOf)
            {
                if (allOfSchema.Properties is not null)
                {
                    foreach (var prop in allOfSchema.Properties)
                    {
                        schema.Properties.TryAdd(prop.Key, prop.Value);
                    }
                }

                if (allOfSchema.Required is not null)
                {
                    foreach (var req in allOfSchema.Required)
                        schema.Required.Add(req);
                }
            }

            schema.AllOf.Clear();
        }

        return Task.CompletedTask;
    }
}
