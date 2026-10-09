using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace SignIt.Modules.Authentication.Data;

public sealed class AuthDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        document.Info.Title = "SignIt API";
        document.Info.Version = "v1";
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
            Description = "JWT RS256 dari login/refresh; kirim melalui header Authorization. Token tidak disimpan di localStorage."
        };

        foreach (var description in context.DescriptionGroups.SelectMany(group => group.Items))
        {
            if (description.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any()) continue;
            var path = "/" + description.RelativePath;
            if (!document.Paths.TryGetValue(path, out var item) || item.Operations is null) continue;
            var method = new HttpMethod(description.HttpMethod!);
            if (!item.Operations.TryGetValue(method, out var operation)) continue;
            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", document)] = []
            });
        }
        return Task.CompletedTask;
    }
}
