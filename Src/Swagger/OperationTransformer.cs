using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.OpenApi;

namespace FastEndpoints.Swagger;

sealed partial class OperationTransformer(DocumentOptions docOpts, JsonSerializerOptions serializerOptions) : IOpenApiOperationTransformer
{
    static readonly TextInfo _textInfo = CultureInfo.InvariantCulture.TextInfo;
    static readonly string[] _illegalHeaderNames = ["Accept", "Content-Type", "Authorization"];

    [GeneratedRegex("(?<={)(?:.*?)*(?=})")]
    private static partial Regex RouteParamsRegex();

    [GeneratedRegex("(?<={)([^?:}]+)[^}]*(?=})")]
    private static partial Regex RouteConstraintsRegex();

    static readonly Dictionary<string, string> _defaultDescriptions = new()
    {
        { "200", "Success" },
        { "201", "Created" },
        { "202", "Accepted" },
        { "204", "No Content" },
        { "400", "Bad Request" },
        { "401", "Unauthorized" },
        { "403", "Forbidden" },
        { "404", "Not Found" },
        { "405", "Method Not Allowed" },
        { "406", "Not Acceptable" },
        { "429", "Too Many Requests" },
        { "500", "Server Error" }
    };

    public Task TransformAsync(OpenApiOperation op, OpenApiOperationTransformerContext ctx, CancellationToken cancellationToken)
    {
        var apiDescription = ctx.Description;
        var metaData = apiDescription.ActionDescriptor.EndpointMetadata;
        var epDef = metaData.OfType<EndpointDefinition>().SingleOrDefault();

        if (epDef is null)
            return Task.CompletedTask; //this is not a fastendpoint

        var opPath = $"/{StripRouteConstraints(apiDescription.RelativePath!.TrimStart('~').TrimEnd('/'))}";

        var epVer = epDef.Version.Current;
        var startingRelVer = epDef.Version.StartingReleaseVersion;
        var version = $"/{GlobalConfig.VersioningPrefix ?? "v"}{epVer}";
        var routePrefix = "/" + (GlobalConfig.EndpointRoutePrefix ?? "_");
        var bareRoute = opPath.Remove(routePrefix).Remove(version);
        var nameMetaData = metaData.OfType<EndpointNameMetadata>().LastOrDefault();
        var reqContent = op.RequestBody?.Content;

        //set operation id if user has specified
        if (nameMetaData is not null)
            op.OperationId = nameMetaData.EndpointName;

        //set operation tag
        if (docOpts.AutoTagPathSegmentIndex > 0 && !epDef.DontAutoTagEndpoints)
        {
            var overrideVal = metaData.OfType<AutoTagOverride>().SingleOrDefault()?.TagName;
            string? tag = null;

            if (overrideVal is not null)
                tag = TagName(overrideVal, docOpts.TagCase, docOpts.TagStripSymbols);
            else
            {
                var segments = bareRoute.Split('/').Where(s => s != string.Empty).ToArray();
                if (segments.Length >= docOpts.AutoTagPathSegmentIndex)
                    tag = TagName(segments[docOpts.AutoTagPathSegmentIndex - 1], docOpts.TagCase, docOpts.TagStripSymbols);
            }
            if (tag is not null)
                op.Tags.Add(new OpenApiTagReference(tag));
        }

        //this will be later removed from document transformer. this info is needed by the document transformer.
        op.Tags.Add(new OpenApiTagReference($"|{apiDescription.HttpMethod}:{bareRoute}|{epVer}|{startingRelVer}|{epDef.Version.DeprecatedAt}"));

        //handle responses
        if (op.Responses.Count > 0)
        {
            var metas = metaData
                        .OfType<IProducesResponseTypeMetadata>()
                        .GroupBy(
                            m => m.StatusCode,
                            (k, g) =>
                            {
                                var meta = g.Last();
                                object? example = null;
                                _ = epDef.EndpointSummary?.ResponseExamples.TryGetValue(k, out example);
                                example = meta.GetExampleFromMetaData() ?? example;
                                var exampleNode = example is not null
                                    ? JsonSerializer.SerializeToNode(example, serializerOptions)
                                    : null;

                                return new
                                {
                                    key = k.ToString(),
                                    cTypes = meta.ContentTypes,
                                    example = exampleNode,
                                    usrHeaders = epDef.EndpointSummary?.ResponseHeaders.Where(h => h.StatusCode == k).ToArray(),
                                    tDto = meta.Type
                                };
                            })
                        .ToDictionary(x => x.key);

            if (metas.Count > 0)
            {
                foreach (var rsp in op.Responses)
                {
                    if (!metas.TryGetValue(rsp.Key, out var x))
                        continue;

                    var concreteRsp = (OpenApiResponse)rsp.Value;

                    var mediaType = concreteRsp.Content?.FirstOrDefault().Value;

                    //set user provided response examples
                    if (mediaType is not null && x.example is not null)
                        mediaType.Example = x.example;

                    //set user provided response headers from dto [ToHeader] properties
                    if (x.tDto is not null)
                    {
                        foreach (var p in x.tDto
                                           .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                                           .Where(p => p.IsDefined(Types.ToHeaderAttribute)))
                        {
                            var headerName = p.GetCustomAttribute<ToHeaderAttribute>()?.HeaderName ?? p.Name.ApplyPropNamingPolicy(docOpts);
                            var summaryTag = p.GetXmlDocsSummary();
                            var schema = Extensions.CreateSchemaForType(p.PropertyType);
                            concreteRsp.Headers ??= new Dictionary<string, IOpenApiHeader>();
                            var header = new OpenApiHeader
                            {
                                Description = summaryTag,
                                Schema = schema
                            };
                            header.Example = p.GetExampleJsonNode(serializerOptions) ?? schema.GenerateSampleJson();
                            concreteRsp.Headers[headerName] = header;
                        }
                    }

                    if (x.usrHeaders?.Length > 0)
                    {
                        concreteRsp.Headers ??= new Dictionary<string, IOpenApiHeader>();
                        foreach (var hdr in x.usrHeaders)
                        {
                            var hdrObj = new OpenApiHeader
                            {
                                Description = hdr.Description,
                                Schema = hdr.Example is not null ? Extensions.CreateSchemaForType(hdr.Example.GetType()) : null
                            };
                            hdrObj.Example = hdr.Example is not null ? JsonSerializer.SerializeToNode(hdr.Example, serializerOptions) : null;
                            concreteRsp.Headers[hdr.HeaderName] = hdrObj;
                        }
                    }

                    //fix response content-types not displaying correctly
                    if (mediaType is not null && x.cTypes.Any())
                    {
                        concreteRsp.Content?.Clear();
                        concreteRsp.Content ??= new Dictionary<string, OpenApiMediaType>();
                        foreach (var ct in x.cTypes)
                            concreteRsp.Content[ct] = mediaType;
                    }

                    //fix byte[] format
                    if (concreteRsp.Content is not null)
                    {
                        foreach (var content in concreteRsp.Content.Values)
                        {
                            if (content.Schema is OpenApiSchema byteSchema && byteSchema.Type?.HasFlag(JsonSchemaType.String) == true && byteSchema.Format == "byte")
                                byteSchema.Format = "binary";
                        }
                    }
                }
            }
        }

        //set endpoint summary & description
        op.Summary = epDef.EndpointSummary?.Summary ?? epDef.EndpointType.GetSummary();
        op.Description = epDef.EndpointSummary?.Description ?? epDef.EndpointType.GetDescription();

        //set endpoint deprecated status when marked with [Obsolete] attribute
        if (epDef.EndpointType.GetCustomAttribute<ObsoleteAttribute>() is not null)
            op.Deprecated = true;

        //set response descriptions
        foreach (var oaResp in op.Responses.Where(r => string.IsNullOrWhiteSpace(r.Value.Description)).ToList())
        {
            if (_defaultDescriptions.TryGetValue(oaResp.Key, out var description))
                oaResp.Value.Description = description;

            var statusCode = Convert.ToInt32(oaResp.Key);

            if (epDef.EndpointSummary?.Responses.ContainsKey(statusCode) is true)
                oaResp.Value.Description = epDef.EndpointSummary.Responses[statusCode];

            if (epDef.EndpointSummary?.ResponseParams.ContainsKey(statusCode) is true)
            {
                var propDescriptions = epDef.EndpointSummary.ResponseParams[statusCode];
                foreach (var prop in oaResp.GetAllProperties())
                {
                    if (propDescriptions.TryGetValue(prop.Key, out var responseDescription))
                        prop.Value.Description = responseDescription;
                }
            }
        }

        if (GlobalConfig.IsUsingAspVersioning)
        {
            for (var i = apiDescription.ParameterDescriptions.Count - 1; i >= 0; i--)
            {
                if (apiDescription.ParameterDescriptions[i].Source != Microsoft.AspNetCore.Mvc.ModelBinding.BindingSource.Body)
                    apiDescription.ParameterDescriptions.RemoveAt(i);
            }
        }

        var reqDtoType = apiDescription.ParameterDescriptions.FirstOrDefault()?.Type;
        var reqDtoIsList = reqDtoType?.GetInterfaces().Contains(Types.IEnumerable);
        var isGetRequest = apiDescription.HttpMethod == "GET";
        var reqDtoProps = reqDtoIsList is true
                              ? null
                              : reqDtoType?.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy).ToList();

        if (reqDtoType != Types.EmptyRequest && reqDtoProps?.Count == 0 && !GlobalConfig.AllowEmptyRequestDtos)
        {
            throw new NotSupportedException(
                "Request DTOs without any publicly accessible properties are not supported. " +
                $"Offending Endpoint: [{epDef.EndpointType.FullName}] " +
                $"Offending DTO type: [{reqDtoType!.FullName}]");
        }

        var reqParamDescriptions = new Dictionary<string, ParamDescription>(StringComparer.OrdinalIgnoreCase);

        if (reqContent is not null)
        {
            foreach (var c in reqContent)
            {
                foreach (var prop in c.GetAllProperties())
                {
                    reqParamDescriptions[prop.Key] = new(
                        prop.Value.Description,
                        prop.Value is OpenApiSchema s ? s.Example : null);
                }
            }
        }

        if (epDef.EndpointSummary is not null)
        {
            foreach (var param in epDef.EndpointSummary.Params)
                reqParamDescriptions.GetOrAdd(param.Key, new()).Description = param.Value;
        }

        if (epDef.EndpointSummary?.RequestExamples.Count is > 0)
        {
            var example = epDef.EndpointSummary.RequestExamples.First().Value;

            if (example is not IEnumerable)
            {
                var jsonNode = JsonSerializer.SerializeToNode(example, serializerOptions);

                if (jsonNode is JsonObject jsonObj)
                {
                    foreach (var p in jsonObj)
                        reqParamDescriptions.GetOrAdd(p.Key, new()).Example = p.Value?.DeepClone();
                }
            }
        }

        if (reqContent is not null)
        {
            foreach (var c in reqContent)
            {
                foreach (var prop in c.GetAllRequestProperties())
                {
                    if (!reqParamDescriptions.TryGetValue(prop.Key, out var x))
                        continue;

                    prop.Value.Description = x.Description;
                    if (prop.Value is OpenApiSchema propSchema)
                        propSchema.Example = x.Example;
                }
            }
        }

        var propsToRemoveFromExample = new List<string>();

        if (reqDtoProps is not null)
        {
            foreach (var p in reqDtoProps.Where(
                                             p => p.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition == JsonIgnoreCondition.Always ||
                                                  p.IsDefined(Types.HideFromDocsAttribute) ||
                                                  p.GetSetMethod()?.IsPublic is not true)
                                         .ToArray())
            {
                RemovePropFromRequestBodyContent(p.Name, reqContent, propsToRemoveFromExample, docOpts);
                reqDtoProps.Remove(p);
            }
        }

        //add path params for each route param
        var reqParams = RouteParamsRegex()
                        .Matches(opPath)
                        .Select(
                            m =>
                            {
                                var pInfo = reqDtoProps?.SingleOrDefault(
                                    p =>
                                    {
                                        var pName = p.GetCustomAttribute<BindFromAttribute>()?.Name ?? p.Name;

                                        if (!string.Equals(pName, m.Value, StringComparison.OrdinalIgnoreCase))
                                            return false;

                                        RemovePropFromRequestBodyContent(p.Name, reqContent, propsToRemoveFromExample, docOpts);

                                        return true;
                                    });

                                return CreateParam(ParameterLocation.Path, pInfo, m.Value, true, reqParamDescriptions, docOpts, opPath);
                            })
                        .ToList();

        //add query params
        if (reqDtoType is not null)
        {
            var qParams = reqDtoProps?
                          .Where(p => ShouldAddQueryParam(p, reqParams, isGetRequest && !docOpts.EnableGetRequestsWithBody, docOpts))
                          .Select(
                              p =>
                              {
                                  RemovePropFromRequestBodyContent(p.Name, reqContent, propsToRemoveFromExample, docOpts);
                                  return CreateParam(ParameterLocation.Query, p, descriptions: reqParamDescriptions, docOpts: docOpts, opPath: opPath);
                              })
                          .ToList();

            if (qParams?.Count > 0)
                reqParams.AddRange(qParams);
        }

        //add request params depending on [From*] attribute annotations
        if (reqDtoProps is not null)
        {
            foreach (var p in reqDtoProps)
            {
                foreach (var attribute in p.GetCustomAttributes())
                {
                    switch (attribute)
                    {
                        case FromHeaderAttribute hAttrib:
                        {
                            var pName = hAttrib.HeaderName ?? p.Name;

                            if (_illegalHeaderNames.Any(n => n.Equals(pName, StringComparison.OrdinalIgnoreCase)))
                            {
                                RemovePropFromRequestBodyContent(p.Name, reqContent, propsToRemoveFromExample, docOpts);
                                continue;
                            }

                            reqParams.Add(CreateParam(ParameterLocation.Header, p, pName, hAttrib.IsRequired, reqParamDescriptions, docOpts, opPath));

                            if (hAttrib.IsRequired || hAttrib.RemoveFromSchema)
                                RemovePropFromRequestBodyContent(p.Name, reqContent, propsToRemoveFromExample, docOpts);

                            break;
                        }

                        case FromCookieAttribute cAttrib:
                        {
                            var pName = cAttrib.CookieName ?? p.Name;
                            reqParams.Add(CreateParam(ParameterLocation.Cookie, p, pName, cAttrib.IsRequired, reqParamDescriptions, docOpts, opPath));

                            if (cAttrib.IsRequired || cAttrib.RemoveFromSchema)
                                RemovePropFromRequestBodyContent(p.Name, reqContent, propsToRemoveFromExample, docOpts);

                            break;
                        }

                        case FromClaimAttribute cAttrib when cAttrib.IsRequired || cAttrib.RemoveFromSchema:
                        case HasPermissionAttribute pAttrib when pAttrib.IsRequired || pAttrib.RemoveFromSchema:
                            RemovePropFromRequestBodyContent(p.Name, reqContent, propsToRemoveFromExample, docOpts);
                            break;
                    }
                }
            }
        }

        //add idempotency header param if applicable
        if (epDef.IdempotencyOptions is not null)
        {
            var prm = CreateParam(ParameterLocation.Header, null, epDef.IdempotencyOptions.HeaderName, true, reqParamDescriptions, docOpts, opPath);
            prm.Example = epDef.IdempotencyOptions.SwaggerExampleGenerator is not null
                ? JsonSerializer.SerializeToNode(epDef.IdempotencyOptions.SwaggerExampleGenerator(), serializerOptions)
                : null;
            prm.Description = epDef.IdempotencyOptions.SwaggerHeaderDescription;
            if (epDef.IdempotencyOptions.SwaggerHeaderType is not null)
                prm.Schema = Extensions.CreateSchemaForType(epDef.IdempotencyOptions.SwaggerHeaderType);
            reqParams.Add(prm);
        }

        foreach (var p in reqParams)
        {
            if (GlobalConfig.IsUsingAspVersioning)
            {
                for (var i = op.Parameters.Count - 1; i >= 0; i--)
                {
                    var prm = op.Parameters[i];
                    if (prm.Name == p.Name && prm.In == p.In)
                        op.Parameters.RemoveAt(i);
                }
            }
            op.Parameters.Add(p);
        }

        //remove request body if GET or no properties left
        if ((isGetRequest && !docOpts.EnableGetRequestsWithBody) || reqContent?.HasNoProperties() is true)
        {
            if (reqDtoIsList is false)
            {
                op.RequestBody = null;
            }
        }

        //replace body if [FromBody] prop exists
        var fromBodyProp = reqDtoProps?.FirstOrDefault(p => p.IsDefined(Types.FromBodyAttribute, false));
        if (fromBodyProp is not null && op.RequestBody is not null)
        {
            var bodyParam = CreateParam(ParameterLocation.Cookie /*placeholder*/, fromBodyProp, fromBodyProp.Name, true, reqParamDescriptions, docOpts, opPath);
            var firstContent = op.RequestBody.Content?.FirstOrDefault().Value;
            if (firstContent is not null)
            {
                firstContent.Schema = bodyParam.Schema;
                ((OpenApiRequestBody)op.RequestBody).Required = bodyParam.Required;
                op.RequestBody.Description = bodyParam.Description;
            }
        }

        //replace body if [FromForm] prop exists
        var fromFormProp = reqDtoProps?.FirstOrDefault(p => p.IsDefined(Types.FromFormAttribute, false));
        if (fromFormProp is not null && op.RequestBody is not null)
        {
            var bodyParam = CreateParam(ParameterLocation.Cookie /*placeholder*/, fromFormProp, fromFormProp.Name, true, reqParamDescriptions, docOpts, opPath);
            var firstContent = op.RequestBody.Content?.FirstOrDefault().Value;
            if (firstContent is not null)
            {
                firstContent.Schema = bodyParam.Schema;
                ((OpenApiRequestBody)op.RequestBody).Required = bodyParam.Required;
                op.RequestBody.Description = bodyParam.Description;
            }
        }

        //set request examples
        if (epDef.EndpointSummary?.RequestExamples.Count > 0)
        {
            var exCount = epDef.EndpointSummary!.RequestExamples.Count;

            if (exCount == 1)
            {
                var requestBody = op.RequestBody?.Content?.FirstOrDefault().Value;
                if (requestBody is not null)
                    requestBody.Example = GetExampleObjectFrom(epDef.EndpointSummary.RequestExamples.First());
            }
            else
            {
                foreach (var group in epDef.EndpointSummary.RequestExamples.GroupBy(e => e.Label).Where(g => g.Count() > 1))
                {
                    var i = 1;
                    foreach (var ex in group)
                    {
                        ex.Label += $" {i}";
                        i++;
                    }
                }

                var firstContentType = reqContent?.FirstOrDefault().Value;
                if (firstContentType is not null)
                {
                    firstContentType.Examples ??= new Dictionary<string, IOpenApiExample>();
                    foreach (var example in epDef.EndpointSummary.RequestExamples)
                    {
                        var oaExample = new OpenApiExample
                        {
                            Summary = example.Summary,
                            Description = example.Description,
                        };
                        oaExample.Value = GetExampleObjectFrom(example);
                        firstContentType.Examples[example.Label] = oaExample;
                    }
                }
            }

            JsonNode? GetExampleObjectFrom(RequestExample? requestExample)
            {
                if (requestExample is null)
                    return null;

                var input = requestExample.Value;
                var tInput = input.GetType();

                if (fromBodyProp is not null)
                {
                    var pFromBody = tInput.GetProperty(fromBodyProp.Name);
                    input = pFromBody?.GetValue(input) ?? input;
                    tInput = input.GetType();
                }

                if (fromFormProp is not null)
                {
                    var pFromForm = tInput.GetProperty(fromFormProp.Name);
                    input = pFromForm?.GetValue(input) ?? input;
                    tInput = input.GetType();
                }

                var jsonNode = JsonSerializer.SerializeToNode(input, serializerOptions);

                if (jsonNode is JsonObject jsonObj)
                {
                    foreach (var propName in propsToRemoveFromExample)
                    {
                        var keyToRemove = jsonObj.FirstOrDefault(p => string.Equals(p.Key, propName, StringComparison.OrdinalIgnoreCase)).Key;
                        if (keyToRemove is not null)
                            jsonObj.Remove(keyToRemove);
                    }
                }

                return jsonNode;
            }
        }

        return Task.CompletedTask;
    }

    static bool ShouldAddQueryParam(PropertyInfo prop, List<OpenApiParameter> reqParams, bool isGetRequest, DocumentOptions docOpts)
    {
        var paramName = prop.Name.ApplyPropNamingPolicy(docOpts);

        foreach (var attribute in prop.GetCustomAttributes())
        {
            switch (attribute)
            {
                case BindFromAttribute bAtt:
                    paramName = bAtt.Name;
                    break;
                case FromHeaderAttribute:
                    return false;
                case FromClaimAttribute cAttrib:
                    return !cAttrib.IsRequired;
                case HasPermissionAttribute pAttrib:
                    return !pAttrib.IsRequired;
            }
        }

        return
            (isGetRequest && !reqParams.Any(rp => rp.Name.Equals(paramName, StringComparison.OrdinalIgnoreCase))) ||
            prop.IsDefined(Types.QueryParamAttribute);
    }

    static void RemovePropFromRequestBodyContent(string propName,
                                                 IDictionary<string, OpenApiMediaType>? content,
                                                 List<string> propsToRemoveFromExample,
                                                 DocumentOptions docOpts)
    {
        if (content is null)
            return;

        propName = propName.ApplyPropNamingPolicy(docOpts);
        propsToRemoveFromExample.Add(propName);

        foreach (var c in content)
        {
            if (c.Value.Schema is OpenApiSchema concreteSchema)
                RemoveFromSchema(concreteSchema, propName);
        }

        static void RemoveFromSchema(OpenApiSchema schema, string key)
        {
            schema.Properties?.Remove(key);
            schema.Required?.Remove(key);

            if (schema.AllOf is not null)
            {
                foreach (var s in schema.AllOf)
                {
                    if (s is OpenApiSchema allOfSchema)
                        RemoveFromSchema(allOfSchema, key);
                }
            }
        }
    }

    static string StripRouteConstraints(string relativePath)
    {
        var parts = relativePath.Split('/');
        for (var i = 0; i < parts.Length; i++)
            parts[i] = RouteConstraintsRegex().Replace(parts[i], "$1");
        return string.Join("/", parts);
    }

    static string TagName(string input, TagCase tagCase, bool stripSymbols)
    {
        return StripSymbols(
            tagCase switch
            {
                TagCase.None => input,
                TagCase.TitleCase => _textInfo.ToTitleCase(input),
                TagCase.LowerCase => _textInfo.ToLower(input),
                _ => input
            });

        string StripSymbols(string val)
            => stripSymbols ? Regex.Replace(val, "[^a-zA-Z0-9]", "") : val;
    }

    static OpenApiParameter CreateParam(ParameterLocation kind,
                                        PropertyInfo? prop = null,
                                        string? paramName = null,
                                        bool? isRequired = null,
                                        Dictionary<string, ParamDescription>? descriptions = null,
                                        DocumentOptions? docOpts = null,
                                        string? opPath = null)
    {
        paramName = paramName?.ApplyPropNamingPolicy(docOpts!) ??
                    prop?.GetCustomAttribute<BindFromAttribute>()?.Name ??
                    prop?.Name.ApplyPropNamingPolicy(docOpts!) ?? throw new InvalidOperationException("param name is required!");

        var propType = prop?.PropertyType ?? typeof(string);

        if (propType.Name.EndsWith("HeaderValue"))
            propType = Types.String;

        var schema = Extensions.CreateSchemaForType(propType);

        var defaultValFromCtorArg = prop?.GetParentCtorDefaultValue();
        bool? hasDefaultValFromCtorArg = defaultValFromCtorArg is not null ? true : null;

        var isNullable = prop?.IsNullable();

        var prm = new OpenApiParameter
        {
            Name = paramName,
            In = kind,
            Schema = schema,
            Required = isRequired ?? !hasDefaultValFromCtorArg ?? !(isNullable ?? true),
            Description = descriptions?.GetValueOrDefault(prop?.Name ?? paramName)?.Description
        };

        if (prop?.GetCustomAttribute<DefaultValueAttribute>()?.Value is { } defVal)
            ((OpenApiSchema)prm.Schema).Default = JsonSerializer.SerializeToNode(defVal);
        else if (defaultValFromCtorArg is not null)
            ((OpenApiSchema)prm.Schema).Default = JsonSerializer.SerializeToNode(defaultValFromCtorArg);

        if (descriptions?.TryGetValue(prop?.Name ?? prm.Name, out var desc) is true && desc?.Example is not null)
            prm.Example = desc.Example;
        else
            prm.Example = prop?.GetExampleJsonNode(null!);

        return prm;
    }
}

sealed class ParamDescription(string? description = null, JsonNode? example = null)
{
    public string? Description { get; set; } = description;
    public JsonNode? Example { get; set; } = example;
}
