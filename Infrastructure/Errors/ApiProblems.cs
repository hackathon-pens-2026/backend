using Microsoft.AspNetCore.Mvc;

namespace SignIt.Infrastructure.Errors;

public static class ApiProblems
{
    public static ProblemDetails Create(HttpContext context, int status, string code, string message)
        => new()
        {
            Status = status, Title = message, Type = $"urn:signit:error:{code}",
            Instance = context.Request.Path,
            Extensions = { ["code"] = code, ["traceId"] = context.TraceIdentifier }
        };

    public static Task WriteAsync(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(Create(context, status, code, message),
            options: null, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
    }
}
