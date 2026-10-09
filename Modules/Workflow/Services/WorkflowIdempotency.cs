using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Persistence;
using SignIt.Modules.Letters.Models;

namespace SignIt.Modules.Workflow.Services;

// Audit and state are committed together under the same letter lock; no separate success transaction.
public static class WorkflowIdempotency
{
    private sealed record Receipt<T>(string Fingerprint, T Result);
    public static string Correlation(Guid actor, Guid entity, string operation, string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 80 || key.Any(char.IsControl))
            throw WorkflowTaskAccess.Error(DomainErrorKind.Validation, "idempotency_key_required", "Idempotency-Key wajib, maksimal 80 karakter.");
        return Hash($"{actor}:{entity}:{operation}:{key}");
    }
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static async Task<T?> ReplayAsync<T>(AppDbContext db, Guid entity, string operation, string correlation, object payload, CancellationToken ct) where T : class
    {
        var audit = await db.AuditLogs.AsNoTracking().SingleOrDefaultAsync(x => x.EntityId == entity && x.Action == operation && x.CorrelationId == correlation, ct);
        if (audit == null) return null;
        var receipt = JsonSerializer.Deserialize<Receipt<T>>(audit.Details!)!;
        if (receipt.Fingerprint != Hash(JsonSerializer.Serialize(payload)))
            throw WorkflowTaskAccess.Error(DomainErrorKind.Conflict, "idempotency_payload_conflict", "Key sudah dipakai untuk payload berbeda.");
        return receipt.Result;
    }
    public static void Record<T>(AppDbContext db, Guid actor, Guid entity, Guid revision, string operation,
        string correlation, object payload, T result, DateTimeOffset now)
        => db.AuditLogs.Add(AuditLog.Record(actor, operation, "Workflow", entity, revision, now, correlation,
            JsonSerializer.Serialize(new Receipt<T>(Hash(JsonSerializer.Serialize(payload)), result))));
}
