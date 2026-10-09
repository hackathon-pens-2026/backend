using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi;

namespace SignIt.Modules.Authentication.Data;

public sealed class AuthErrorsTransformer : IOpenApiOperationTransformer
{
    public async Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken ct)
    {
        var schema = await context.GetOrCreateSchemaAsync(typeof(ProblemDetails), null, ct);
        var errors = new Dictionary<string, string>
        {
            ["400"] = "validation_failed, invalid_request, password_policy_failed, atau invalid_reset_token",
            ["409"] = "auth_state_conflict",
            ["503"] = "persistence_unavailable",
            ["500"] = "internal_error"
        };
        if (!context.Description.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any())
        {
            errors["401"] = "unauthorized";
            errors["403"] = "forbidden";
        }
        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<EnableRateLimitingAttribute>().Any())
            errors["429"] = "rate_limit_exceeded";

        operation.Responses ??= new OpenApiResponses();
        // Include controller-declared 401 responses with the actual problem media type too.
        foreach (var status in operation.Responses.Keys.Where(key => int.TryParse(key, out var code) && code >= 400).ToArray())
            errors.TryAdd(status, "Kesalahan autentikasi");
        foreach (var (status, description) in errors)
            operation.Responses[status] = new OpenApiResponse
            {
                Description = description,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/problem+json"] = new OpenApiMediaType { Schema = schema }
                }
            };
    }
}
