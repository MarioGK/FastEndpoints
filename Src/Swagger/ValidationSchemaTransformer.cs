// Original: https://github.com/zymlabs/nswag-fluentvalidation
// MIT License
// Copyright (c) 2019 Zym Labs LLC

using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using FastEndpoints.Swagger.ValidationProcessor;
using FastEndpoints.Swagger.ValidationProcessor.Extensions;
using FluentValidation;
using FluentValidation.Internal;
using FluentValidation.Validators;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;

namespace FastEndpoints.Swagger;

sealed class ValidationSchemaTransformer : IOpenApiSchemaTransformer
{
    IServiceResolver? _serviceResolver;
    ILogger<ValidationSchemaTransformer>? _logger;
    static Type[]? _validatorTypes;
    FluentValidationRule[]? _rules;
    readonly Dictionary<string, IValidator> _childAdaptorValidators = new();
    bool _initialized;

    void Initialize(IServiceProvider services)
    {
        if (_initialized) return;

        _serviceResolver = services.GetRequiredService<IServiceResolver>();
        _logger = services.GetRequiredService<ILogger<ValidationSchemaTransformer>>();
        _rules = CreateDefaultRules();
        _validatorTypes ??= _serviceResolver.Resolve<EndpointData>().Found
                                            .Where(e => e.ValidatorType != null)
                                            .Select(e => e.ValidatorType!)
                                            .Distinct()
                                            .ToArray();

        if (_validatorTypes?.Length is null or 0)
            _logger.NoValidatorsFound();

        _initialized = true;
    }

    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        Initialize(context.ApplicationServices);

        if (_validatorTypes?.Length is null or 0)
            return Task.CompletedTask;

        var tRequest = context.JsonTypeInfo.Type;

        if (tRequest is null || schema.Properties is null || schema.Properties.Count == 0)
            return Task.CompletedTask;

        using var scope = _serviceResolver!.CreateScope();

        foreach (var tValidator in _validatorTypes)
        {
            try
            {
                if (tValidator.BaseType?.GenericTypeArguments.FirstOrDefault() == tRequest)
                {
                    var validator = _serviceResolver.CreateInstance(tValidator, scope.ServiceProvider) ??
                                    throw new InvalidOperationException($"Unable to instantiate validator {tValidator.Name}!");
                    ApplyValidator(schema, (IValidator)validator, "", scope.ServiceProvider);
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger!.ExceptionProcessingValidator(ex, tValidator.Name);
            }
        }

        return Task.CompletedTask;
    }

    void ApplyValidator(OpenApiSchema schema, IValidator validator, string propertyPrefix, IServiceProvider services)
    {
        var rulesDict = validator.GetDictionaryOfRules();
        ApplyRulesToSchema(schema, rulesDict, propertyPrefix, services);
        ApplyRulesFromIncludedValidators(schema, validator, services);
    }

    void ApplyRulesToSchema(OpenApiSchema? schema,
                            ReadOnlyDictionary<string, List<IValidationRule>> rulesDict,
                            string propertyPrefix,
                            IServiceProvider services)
    {
        if (schema?.Properties is null)
            return;

        foreach (var schemaProperty in schema.Properties.Keys)
            TryApplyValidation(schema, rulesDict, schemaProperty, propertyPrefix, services);

        // Apply to allOf schemas (inherited)
        if (schema.AllOf is { Count: > 0 })
        {
            foreach (var allOfSchema in schema.AllOf)
            {
                if (allOfSchema is OpenApiSchema concreteAllOf)
                    ApplyRulesToSchema(concreteAllOf, rulesDict, propertyPrefix, services);
            }
        }
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(ChildValidatorAdaptor<,>))]
    void ApplyRulesFromIncludedValidators(OpenApiSchema schema, IValidator validator, IServiceProvider services)
    {
        if (validator is not IEnumerable<IValidationRule> rules)
            return;

        var childAdapters = rules
                            .Where(rule => rule.HasNoCondition() && rule is IIncludeRule)
                            .SelectMany(includeRule => includeRule.Components.Select(c => c.Validator))
                            .Where(x => x.GetType().IsGenericType && x.GetType().GetGenericTypeDefinition() == typeof(ChildValidatorAdaptor<,>))
                            .ToList();

        foreach (var adapter in childAdapters)
        {
            var adapterMethod = adapter.GetType().GetMethod("GetValidator");

            if (adapterMethod == null)
                continue;

            var validationContext = Activator.CreateInstance(adapterMethod.GetParameters().First().ParameterType, [null!]);

            if (adapterMethod.Invoke(adapter, [validationContext, null!]) is not IValidator includeValidator)
                break;

            ApplyRulesToSchema(schema, includeValidator.GetDictionaryOfRules(), string.Empty, services);
            ApplyRulesFromIncludedValidators(schema, includeValidator, services);
        }
    }

    void TryApplyValidation(OpenApiSchema schema,
                            ReadOnlyDictionary<string, List<IValidationRule>> rulesDict,
                            string propertyName,
                            string parameterPrefix,
                            IServiceProvider services)
    {
        var fullPropertyName = $"{parameterPrefix}{propertyName}";

        if (rulesDict.TryGetValue(fullPropertyName, out var validationRules))
        {
            foreach (var validationRule in validationRules)
                ApplyValidationRule(schema, validationRule, propertyName, services);
        }

        if (schema.Properties.TryGetValue(propertyName, out var property))
        {
            if (property is OpenApiSchema concreteProp && concreteProp.Properties is { Count: > 0 } && concreteProp != schema)
                ApplyRulesToSchema(concreteProp, rulesDict, $"{fullPropertyName}.", services);
        }
    }

    void ApplyValidationRule(OpenApiSchema schema, IValidationRule validationRule, string propertyName, IServiceProvider services)
    {
        foreach (var ruleComponent in validationRule.Components)
        {
            var propertyValidator = ruleComponent.Validator;

            if (propertyValidator.Name == "ChildValidatorAdaptor")
            {
                if (propertyValidator.GetType().Name.StartsWith("PolymorphicValidator"))
                {
                    _logger?.SwaggerWithFluentValidationIntegrationForPolymorphicValidatorsIsNotSupported(propertyValidator.GetType().Name);
                    continue;
                }

                var validatorTypeObj = propertyValidator.GetType().GetProperty("ValidatorType")?.GetValue(propertyValidator);

                if (validatorTypeObj is not Type validatorType)
                    throw new InvalidOperationException("ChildValidatorAdaptor.ValidatorType is null");

                if (!validatorType.IsInterface &&
                    !_childAdaptorValidators.TryGetValue(validatorType.FullName!, out var childValidator))
                {
                    childValidator = _childAdaptorValidators[validatorType.FullName!] =
                                         (IValidator)_serviceResolver!.CreateInstance(validatorType, services);
                }
                else
                    continue;

                if (schema.Properties.TryGetValue(propertyName, out var childSchema) && childSchema is OpenApiSchema concreteChild)
                {
                    var targetSchema = concreteChild.Type?.HasFlag(JsonSchemaType.Array) == true && concreteChild.Items is OpenApiSchema itemsSchema
                        ? itemsSchema
                        : concreteChild;
                    ApplyValidator(targetSchema, childValidator, string.Empty, services);
                }

                continue;
            }

            foreach (var rule in _rules!)
            {
                if (!rule.Matches(propertyValidator))
                    continue;

                try
                {
                    rule.Apply(new(schema, propertyName, propertyValidator, ruleComponent.HasCondition()));
                }
                catch
                {
                    //do nothing
                }
            }
        }
    }

    static FluentValidationRule[] CreateDefaultRules()
        =>
        [
            new("Required")
            {
                Matches = propertyValidator => propertyValidator is INotNullValidator or INotEmptyValidator,
                Apply = context =>
                        {
                            var schema = context.Schema;
                            schema.Required ??= new HashSet<string>();
                            if (!schema.Required.Contains(context.PropertyKey) && !context.HasCondition)
                                schema.Required.Add(context.PropertyKey);
                        }
            },
            new("NotNull")
            {
                Matches = propertyValidator => propertyValidator is INotNullValidator,
                Apply = context =>
                        {
                            if (context.HasCondition)
                                return;

                            if (context.Schema.Properties?.TryGetValue(context.PropertyKey, out var prop) is true && prop is OpenApiSchema concreteProp)
                            {
                                // Remove Null from type flags if present
                                if (concreteProp.Type?.HasFlag(JsonSchemaType.Null) == true)
                                    concreteProp.Type &= ~JsonSchemaType.Null;
                            }
                        }
            },
            new("NotEmpty")
            {
                Matches = propertyValidator => propertyValidator is INotEmptyValidator,
                Apply = context =>
                        {
                            if (context.HasCondition)
                                return;

                            if (context.Schema.Properties?.TryGetValue(context.PropertyKey, out var prop) is true && prop is OpenApiSchema concreteProp)
                            {
                                concreteProp.MinLength = 1;

                                if (concreteProp.Type?.HasFlag(JsonSchemaType.Null) == true)
                                    concreteProp.Type &= ~JsonSchemaType.Null;
                            }
                        }
            },
            new("Length")
            {
                Matches = propertyValidator => propertyValidator is ILengthValidator,
                Apply = context =>
                        {
                            if (context.Schema.Properties?.TryGetValue(context.PropertyKey, out var prop) is not true || prop is not OpenApiSchema concreteProp)
                                return;

                            var lengthValidator = (ILengthValidator)context.PropertyValidator;
                            if (lengthValidator.Max > 0)
                                concreteProp.MaxLength = lengthValidator.Max;
                            if (lengthValidator.GetType() == typeof(MinimumLengthValidator<>) ||
                                lengthValidator.GetType() == typeof(ExactLengthValidator<>) ||
                                concreteProp.MinLength is null or 1)
                                concreteProp.MinLength = lengthValidator.Min;
                        }
            },
            new("Pattern")
            {
                Matches = propertyValidator => propertyValidator is IRegularExpressionValidator,
                Apply = context =>
                        {
                            if (context.Schema.Properties?.TryGetValue(context.PropertyKey, out var prop) is true && prop is OpenApiSchema concreteProp)
                            {
                                var regularExpressionValidator = (IRegularExpressionValidator)context.PropertyValidator;
                                concreteProp.Pattern = regularExpressionValidator.Expression;
                            }
                        }
            },
            new("Comparison")
            {
                Matches = propertyValidator => propertyValidator is IComparisonValidator,
                Apply = context =>
                        {
                            if (context.Schema.Properties?.TryGetValue(context.PropertyKey, out var prop) is not true || prop is not OpenApiSchema concreteProp)
                                return;

                            var comparisonValidator = (IComparisonValidator)context.PropertyValidator;

                            if (comparisonValidator.ValueToCompare.IsNumeric())
                            {
                                var valueToCompare = Convert.ToDecimal(comparisonValidator.ValueToCompare);

                                if (comparisonValidator.Comparison == Comparison.GreaterThanOrEqual)
                                    concreteProp.Minimum = valueToCompare.ToString(CultureInfo.InvariantCulture);
                                else if (comparisonValidator.Comparison == Comparison.GreaterThan)
                                {
                                    concreteProp.ExclusiveMinimum = valueToCompare.ToString(CultureInfo.InvariantCulture);
                                }
                                else if (comparisonValidator.Comparison == Comparison.LessThanOrEqual)
                                    concreteProp.Maximum = valueToCompare.ToString(CultureInfo.InvariantCulture);
                                else if (comparisonValidator.Comparison == Comparison.LessThan)
                                {
                                    concreteProp.ExclusiveMaximum = valueToCompare.ToString(CultureInfo.InvariantCulture);
                                }
                            }
                        }
            },
            new("Between")
            {
                Matches = propertyValidator => propertyValidator is IBetweenValidator,
                Apply = context =>
                        {
                            if (context.Schema.Properties?.TryGetValue(context.PropertyKey, out var prop) is not true || prop is not OpenApiSchema concreteProp)
                                return;

                            var betweenValidator = (IBetweenValidator)context.PropertyValidator;

                            if (betweenValidator.From.IsNumeric())
                            {
                                if (betweenValidator.GetType().IsSubClassOfGeneric(typeof(ExclusiveBetweenValidator<,>)))
                                    concreteProp.ExclusiveMinimum = Convert.ToDecimal(betweenValidator.From).ToString(CultureInfo.InvariantCulture);
                                else
                                    concreteProp.Minimum = Convert.ToDecimal(betweenValidator.From).ToString(CultureInfo.InvariantCulture);
                            }

                            if (betweenValidator.To.IsNumeric())
                            {
                                if (betweenValidator.GetType().IsSubClassOfGeneric(typeof(ExclusiveBetweenValidator<,>)))
                                    concreteProp.ExclusiveMaximum = Convert.ToDecimal(betweenValidator.To).ToString(CultureInfo.InvariantCulture);
                                else
                                    concreteProp.Maximum = Convert.ToDecimal(betweenValidator.To).ToString(CultureInfo.InvariantCulture);
                            }
                        }
            },
            new("AspNetCoreCompatibleEmail")
            {
                Matches = propertyValidator => propertyValidator.GetType().IsSubClassOfGeneric(typeof(AspNetCoreCompatibleEmailValidator<>)),
                Apply = context =>
                        {
                            if (context.Schema.Properties?.TryGetValue(context.PropertyKey, out var prop) is true && prop is OpenApiSchema concreteProp)
                            {
                                concreteProp.Format = "email";
                                concreteProp.Pattern = "^[^@]+@[^@]+$";
                            }
                        }
            }
        ];
}
