using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Persistence;
using SignIt.Infrastructure.Storage;
using SignIt.Modules.Authentication.Services;
using SignIt.Modules.Letters.Models;
using SignIt.Modules.Signatures.Services;
using SignIt.Modules.Workflow.Services;

namespace SignIt.Modules.Signatures.Controllers;

public sealed record RetryFinalizationRequest(Guid ExpectedRevisionId, string ExpectedContentHash, Guid ExpectedVersion);
public sealed record FinalizationDto(Guid LetterId, Guid RevisionId, Guid Version, string Status,
    string? VerificationCode, string? DownloadUrl);

[Authorize]
[ApiController]
[Route("api/v1/letters/{id:guid}/finalization")]
public sealed class LetterFinalizationController(AppDbContext db, IStorageService storage,
    IQrCodeGenerator hashes, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<FinalizationDto>> Get(Guid id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await DescribeAsync(await OwnedAsync(id, false, ct), ct));
    }

    [HttpPost("retry")]
    public async Task<ActionResult<FinalizationDto>> Retry(Guid id, RetryFinalizationRequest request,
        [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct)
    {
        var actor = User.GetUserId();
        var correlation = WorkflowIdempotency.Correlation(actor, id, "letter.finalization_retry", key);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var letter = await OwnedAsync(id, true, ct);
        var replay = await WorkflowIdempotency.ReplayAsync<FinalizationDto>(db, id,
            "letter.finalization_retry", correlation, request, ct);
        if (replay != null) { await transaction.CommitAsync(ct); return Accepted(replay); }
        var revision = await db.LetterRevisions.SingleAsync(x => x.Id == letter.CurrentRevisionId, ct);
        if (letter.Status != LetterStatus.ProcessingFailed || letter.RowVersion != request.ExpectedVersion
            || revision.Id != request.ExpectedRevisionId || revision.ContentHash != request.ExpectedContentHash)
            throw new SignItDomainException(DomainErrorKind.Conflict, "finalization_retry_conflict", "Surat berubah atau tidak dalam status gagal render.");
        var tasks = await db.WorkflowTasks.AsNoTracking().Where(x => x.RevisionId == revision.Id).ToListAsync(ct);
        if (tasks.Count == 0 || tasks.Any(x => !WorkflowTaskAccess.IsDone(x.Status)))
            throw new SignItDomainException(DomainErrorKind.Conflict, "workflow_incomplete", "Tugas belum lengkap.");
        letter.MarkFinalizing();
        var result = await DescribeAsync(letter, ct);
        WorkflowIdempotency.Record(db, actor, id, revision.Id, "letter.finalization_retry", correlation,
            request, result, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Accepted(result);
    }

    [HttpGet("document")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        var letter = await OwnedAsync(id, false, ct);
        var revision = await db.LetterRevisions.AsNoTracking().SingleAsync(x => x.Id == letter.CurrentRevisionId, ct);
        var document = await db.Documents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == revision.FinalDocumentId
            && x.RevisionId == revision.Id && x.Kind == DocumentKind.Final && x.ProcessingState == "Ready", ct);
        if (letter.Status != LetterStatus.Completed || document == null)
            throw new SignItDomainException(DomainErrorKind.Conflict, "final_document_not_ready", "Dokumen final belum siap.");
        var bytes = await storage.ReadBytesAsync(document.StorageKey, ct);
        if (bytes == null || bytes.LongLength != document.Bytes || hashes.ComputeSha256(bytes) != document.Sha256)
            throw new SignItDomainException(DomainErrorKind.ProcessingFailed, "final_document_invalid", "Dokumen final tidak tersedia atau rusak.");
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(bytes, "application/pdf", $"SignIt-{letter.Id:N}.pdf");
    }

    private async Task<LetterRequest> OwnedAsync(Guid id, bool update, CancellationToken ct)
    {
        var actor = User.GetUserId();
        var query = update
            ? db.LetterRequests.FromSqlInterpolated($"SELECT * FROM letter_requests WHERE \"Id\"={id} AND \"SubmittedByUserId\"={actor} FOR UPDATE")
            : db.LetterRequests.Where(x => x.Id == id && x.SubmittedByUserId == actor);
        var letter = await query.SingleOrDefaultAsync(ct)
            ?? throw new SignItDomainException(DomainErrorKind.NotFound, "letter_not_found", "Surat tidak ditemukan.");
        if (update) await db.Entry(letter).ReloadAsync(ct);
        return letter;
    }

    private async Task<FinalizationDto> DescribeAsync(LetterRequest letter, CancellationToken ct)
    {
        var revision = await db.LetterRevisions.AsNoTracking().SingleAsync(x => x.Id == letter.CurrentRevisionId, ct);
        var code = await db.VerificationRecords.AsNoTracking().Where(x => x.RequestId == letter.Id
            && x.FinalDocumentId == revision.FinalDocumentId).Select(x => x.RandomCode).FirstOrDefaultAsync(ct);
        return new(letter.Id, revision.Id, letter.RowVersion, letter.Status.ToString(), code,
            letter.Status == LetterStatus.Completed ? $"/api/v1/letters/{letter.Id}/finalization/document" : null);
    }
}
