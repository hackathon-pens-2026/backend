using Microsoft.EntityFrameworkCore;
using SignIt.Infrastructure.Persistence;
using SignIt.Modules.Letters.Models;

namespace SignIt.Modules.Signatures.Services;

// The persisted Finalizing state is a recoverable job. The processor takes the
// same letter row lock as workflow writers, so separate app instances cannot
// publish competing final documents. No signature is recreated on retry.
public sealed class SignatureFinalizationWorker(IServiceScopeFactory scopes,
    TimeProvider clock, ILogger<SignatureFinalizationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var cutoff = clock.GetUtcNow().AddSeconds(-30);
                var ids = await db.LetterRequests.AsNoTracking()
                    .Where(l => l.Status == LetterStatus.Finalizing || (l.Status == LetterStatus.ProcessingFailed
                        && db.AuditLogs.Count(a => a.EntityId == l.Id && a.RevisionId == l.CurrentRevisionId
                            && a.Action == "letter.processing_failed") < 3
                        && !db.AuditLogs.Any(a => a.EntityId == l.Id && a.RevisionId == l.CurrentRevisionId
                            && a.Action == "letter.processing_failed" && a.AtUtc > cutoff)))
                    .OrderBy(l => l.Id).Select(l => l.Id).Take(10).ToListAsync(stoppingToken);
                foreach (var id in ids)
                {
                    using var itemScope = scopes.CreateScope();
                    await itemScope.ServiceProvider.GetRequiredService<ISignatureWorkflowService>()
                        .RetryFinalizationAsync(id, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Finalization worker gagal: {ErrorType}", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
