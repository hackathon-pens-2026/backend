using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SignIt.Infrastructure.Persistence;
using SignIt.Infrastructure.Storage;
using SignIt.Modules.Letters.Models;
using SignIt.Modules.Templates.Services;

namespace SignIt.Modules.Letters.Services;

public sealed class PreviewWorkerOptions
{
    public bool Enabled { get; set; } = true;
    public int PollSeconds { get; set; } = 2;
    public int RenderTimeoutSeconds { get; set; } = 20;
    public int LeaseSeconds { get; set; } = 60;
}

public sealed class LetterPreviewProcessor(AppDbContext db, ILetterTemplateRenderer renderer,
    IStorageService storage, TimeProvider clock, PreviewWorkerOptions options, ILogger<LetterPreviewProcessor> logger)
{
    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        LetterPreviewJob? claimed;
        Guid lease;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            var now = clock.GetUtcNow();
            claimed = await db.LetterPreviewJobs.FromSqlInterpolated($"SELECT * FROM letter_preview_jobs WHERE \"State\"='Pending' OR (\"State\"='Processing' AND \"LeaseUntil\"<={now}) ORDER BY \"CreatedAt\", \"Id\" FOR UPDATE SKIP LOCKED LIMIT 1").FirstOrDefaultAsync(ct);
            if (claimed is null) { await transaction.CommitAsync(ct); return false; }
            lease = claimed.Claim(now, TimeSpan.FromSeconds(options.LeaseSeconds));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        var jobId = claimed.Id;
        var requestId = claimed.RequestId;
        var revisionId = claimed.RevisionId;
        var inputJson = claimed.InputJson;
        var attempts = claimed.Attempts;
        db.ChangeTracker.Clear();
        RenderedPreview? rendered = null;
        string? failure = null;
        try
        {
            if (attempts > 3) throw new InvalidOperationException("Batas pemulihan job tercapai.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.RenderTimeoutSeconds));
            rendered = renderer.Render(JsonSerializer.Deserialize<PreviewRenderInput>(inputJson)
                ?? throw new InvalidOperationException("Input renderer kosong."), timeout.Token);
            if (rendered.Bytes.Length > 10 * 1024 * 1024) throw new InvalidOperationException("Preview melebihi batas ukuran.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            failure = ex is OperationCanceledException ? "preview_render_timeout" : "preview_render_failed";
            logger.LogWarning("Preview {JobId} gagal: {ErrorType}", jobId, ex.GetType().Name);
        }

        await using var completion = await db.Database.BeginTransactionAsync(ct);
        // Same lock order as queue/submit, preventing deadlocks with concurrent actions.
        var letter = await db.LetterRequests.FromSqlInterpolated($"SELECT * FROM letter_requests WHERE \"Id\"={requestId} FOR UPDATE").SingleAsync(ct);
        var job = await db.LetterPreviewJobs.FromSqlInterpolated($"SELECT * FROM letter_preview_jobs WHERE \"Id\"={jobId} FOR UPDATE").SingleAsync(ct);
        if (job.LeaseToken != lease || job.State != "Processing") { await completion.CommitAsync(ct); return true; }
        var completedAt = clock.GetUtcNow();
        string? storageKey = null;
        if (letter.Status is not (LetterStatus.Draft or LetterStatus.NeedsRevision) || letter.CurrentRevisionId != revisionId
            || await db.WorkflowTasks.AnyAsync(x => x.RevisionId == revisionId, ct))
            job.Supersede(lease, completedAt);
        else if (failure is not null)
            job.Fail(lease, failure, completedAt);
        else
        {
            var documentId = Guid.NewGuid();
            storageKey = $"letters/{requestId:N}/previews/{documentId:N}.pdf";
            await storage.SaveAsync(storageKey, rendered!.Bytes, "application/pdf", ct);
            db.Documents.Add(Document.Create(documentId, revisionId, DocumentKind.Review, storageKey,
                "application/pdf", rendered.Bytes.Length, LetterPreviewService.Hash(rendered.Bytes), completedAt));
            job.Complete(lease, documentId, JsonSerializer.Serialize(rendered.Slots), completedAt);
        }
        db.AuditLogs.Add(AuditLog.Record(letter.SubmittedByUserId, "letter.preview_" + job.State.ToLowerInvariant(),
            "LetterRequest", requestId, revisionId, completedAt, jobId.ToString("N"), job.ErrorCode ?? job.InputHash));
        try
        {
            await db.SaveChangesAsync(ct);
            await completion.CommitAsync(ct);
        }
        catch
        {
            await completion.RollbackAsync(CancellationToken.None);
            if (storageKey is not null) await storage.DeleteAsync(storageKey, CancellationToken.None);
            throw;
        }
        return true;
    }
}

public sealed class LetterPreviewWorker(IServiceScopeFactory scopes, PreviewWorkerOptions options,
    ILogger<LetterPreviewWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                if (await scope.ServiceProvider.GetRequiredService<LetterPreviewProcessor>().ProcessNextAsync(stoppingToken)) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Worker preview belum dapat memproses job: {ErrorType}", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
