using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SignIt.Modules.Authentication.DTOs;

namespace SignIt.Infrastructure.Errors;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, code, message) = exception switch
        {
            SignItDomainException domain => ((int)domain.Kind, domain.Code, domain.Message),
            AuthException auth => (auth.Kind switch
            {
                AuthErrorKind.Validation => 400,
                AuthErrorKind.Unauthorized => 401,
                AuthErrorKind.Conflict => 409,
                _ => 500
            }, auth.Code, auth.Message),
            DbUpdateConcurrencyException => (409, "auth_state_conflict", "Status akun berubah. Ulangi permintaan."),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } }
                => (409, "auth_state_conflict", "Data akun bertentangan. Ulangi permintaan."),
            NpgsqlException or DbUpdateException => (503, "persistence_unavailable", "Penyimpanan akun belum tersedia. Coba kembali nanti."),
            BadHttpRequestException request => (request.StatusCode, "invalid_request", "Permintaan tidak valid atau melebihi batas ukuran."),
            _ => (500, "internal_error", "Terjadi kesalahan internal.")
        };
        if (status >= 500)
            logger.LogError(exception, "API gagal: {ErrorCode}, {ExceptionType}, trace {TraceId}.", code, exception.GetType().Name, context.TraceIdentifier);
        if (status == 401) context.Response.Headers.WWWAuthenticate = "Bearer";
        await ApiProblems.WriteAsync(context, status, code, message);
        return true;
    }
}
