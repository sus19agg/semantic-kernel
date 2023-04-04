﻿// Copyright (c) Microsoft. All rights reserved.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using Microsoft.SemanticKernel.Skills.OpenAPI.Model;

namespace Microsoft.SemanticKernel.Skills.OpenAPI.OpenApi;

/// <summary>
/// Parser for OpenAPI documents.
/// </summary>
internal class OpenApiDocumentParser : IOpenApiDocumentParser
{
    /// <inheritdoc/>
    public IList<RestApiOperation> Parse(Stream stream)
    {
        var openApiDocument = new OpenApiStreamReader().Read(stream, out var diagnostic);

        if (diagnostic.Errors.Any())
        {
            throw new OpenApiDocumentParsingException($"Parsing of '{openApiDocument.Info?.Title}' OpenAPI document failed. Details: {string.Join(';', diagnostic.Errors)}");
        }

        return ExtractRestApiOperations(openApiDocument);
    }

    #region private

    /// <summary>
    /// Parses an OpenApi document and extracts REST API operations.
    /// </summary>
    /// <param name="document">The OpenApi document.</param>
    /// <returns>List of Rest operations.</returns>
    private static IList<RestApiOperation> ExtractRestApiOperations(OpenApiDocument document)
    {
        var result = new List<RestApiOperation>();

        var serverUrl = document.Servers.First().Url;

        foreach (var pathPair in document.Paths)
        {
            var operations = CreateRestApiOperations(serverUrl, pathPair.Key, pathPair.Value);

            result.AddRange(operations);
        }

        return result;
    }

    /// <summary>
    /// Creates REST API operation.
    /// </summary>
    /// <param name="path">Rest resource path.</param>
    /// <param name="pathItem">Rest resource metadata.</param>
    /// <param name="serverUrl">The server url.</param>
    /// <returns>Rest operation.</returns>
    private static IList<RestApiOperation> CreateRestApiOperations(string serverUrl, string path, OpenApiPathItem pathItem)
    {
        var operations = new List<RestApiOperation>();

        foreach (var operationPair in pathItem.Operations)
        {
            var method = operationPair.Key.ToString();

            var operationItem = operationPair.Value;

            var operation = new RestApiOperation(
                operationItem.OperationId,
                serverUrl,
                path,
                new HttpMethod(method),
                operationItem.Description,
                CreateRestApiOperationParameters(operationItem.OperationId, operationItem.Parameters),
                CreateRestApiOperationPayload(operationItem.OperationId, operationItem.RequestBody)
            );

            operations.Add(operation);
        }

        return operations;
    }

    /// <summary>
    /// Creates REST API operation parameters.
    /// </summary>
    /// <param name="operationId">The operation id.</param>
    /// <param name="parameters">The OpenApi parameters.</param>
    /// <returns>The parameters.</returns>
    private static IList<RestApiOperationParameter> CreateRestApiOperationParameters(string operationId, IList<OpenApiParameter> parameters)
    {
        var result = new List<RestApiOperationParameter>();

        foreach (var parameter in parameters)
        {
            if (parameter.In == null)
            {
                throw new OpenApiDocumentParsingException($"Parameter location of {parameter.Name} parameter of {operationId} operation is undefined.");
            }

            var restParameter = new RestApiOperationParameter(
                parameter.Name,
                parameter.Schema.Type,
                parameter.Required,
                (RestApiOperationParameterLocation)parameter.In, //TODO: Do a proper enum mapping,
                (parameter.Schema.Default as OpenApiString)?.Value,
                parameter.Description
            );

            result.Add(restParameter);
        }

        return result;
    }

    /// <summary>
    /// Creates REST API operation payload.
    /// </summary>
    /// <param name="operationId">The operation id.</param>
    /// <param name="requestBody">The OpenApi request body.</param>
    /// <returns>The REST API operation payload.</returns>
    private static RestApiOperationPayload? CreateRestApiOperationPayload(string operationId, OpenApiRequestBody requestBody)
    {
        if (requestBody?.Content == null)
        {
            return null;
        }

        var mediaType = s_supportedMediaTypes.FirstOrDefault(smt => requestBody.Content.ContainsKey(smt));
        if (mediaType == null)
        {
            throw new OpenApiDocumentParsingException($"Neither of the media types of {operationId} is supported.");
        }

        var mediaTypeMetadata = requestBody.Content[mediaType];

        var payloadProperties = GetPayloadProperties(operationId, mediaTypeMetadata.Schema, mediaTypeMetadata.Schema.Required);

        return new RestApiOperationPayload(mediaType, payloadProperties);
    }

    /// <summary>
    /// Returns REST API operation payload properties.
    /// </summary>
    /// <param name="operationId">The operation id.</param>
    /// <param name="schema">An OpenApi document schema representing request body properties.</param>
    /// <param name="requiredProperties">List of required properties.</param>
    /// <param name="level">Current level in OpenApi schema.</param>
    /// <returns>The REST API operation payload properties.</returns>
    private static IList<RestApiOperationPayloadProperty> GetPayloadProperties(string operationId, OpenApiSchema? schema, ISet<string> requiredProperties, int level = 0)
    {
        if (schema == null)
        {
            return new List<RestApiOperationPayloadProperty>();
        }

        if (level > s_payloadPropertiesHierarchyMaxDepth)
        {
            throw new OpenApiDocumentParsingException($"Max level {s_payloadPropertiesHierarchyMaxDepth} of traversing payload properties of {operationId} operation is exceeded.");
        }

        var result = new List<RestApiOperationPayloadProperty>();

        foreach (var propertyPair in schema.Properties)
        {
            var propertyName = propertyPair.Key;

            var propertySchema = propertyPair.Value;

            var property = new RestApiOperationPayloadProperty(
                propertyName,
                propertySchema.Type,
                requiredProperties.Contains(propertyName),
                GetPayloadProperties(operationId, propertySchema, requiredProperties, level + 1),
                propertySchema.Description
            );

            result.Add(property);
        }

        return result;
    }

    /// <summary>
    /// List of supported Media Types.
    /// </summary>
    private static IList<string> s_supportedMediaTypes = new List<string>
    {
        MediaTypeNames.Application.Json
    };

    /// <summary>
    /// Max depth to traverse down OpenApi schema to discover payload properties.
    /// </summary>
    private static int s_payloadPropertiesHierarchyMaxDepth = 10;

    #endregion
}