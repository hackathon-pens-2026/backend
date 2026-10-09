using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Persistence;
using SignIt.Modules.Letters.Models;
using SignIt.Modules.Templates.Services;
using SignIt.Modules.Workflow.Services;
using SignIt.Modules.Workflow.Models;

namespace SignIt.Modules.Letters.Services;

public sealed record SaveDraftRequest(string TypeId, string Title, Dictionary<string, string> Fields);
public sealed record DraftDto(Guid Id, string TypeId, string Title, Guid Version, Guid RevisionId, string ContentHash, string DataJson);
public sealed record EditDraftRequest(Guid ExpectedVersion, Guid ExpectedRevisionId, string ExpectedContentHash,
    string Title, Dictionary<string, string> Fields);
public sealed record CancelLetterRequest(Guid ExpectedVersion, Guid ExpectedRevisionId, string ExpectedContentHash, string Reason);
public sealed record CancelLetterDto(Guid Id, LetterStatus Status, Guid Version);

public sealed class LettersService(AppDbContext db, TemplateCatalog templates, TimeProvider clock)
{
    public async Task<DraftDto> CreateAsync(Guid actor, SaveDraftRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 300)
            throw new SignItDomainException(DomainErrorKind.Validation, "invalid_title", "Judul wajib diisi, maksimal 300 karakter.");
        var template = (await templates.GetAllAsync(ct)).SingleOrDefault(x => x.TypeId == request.TypeId)
            ?? throw new SignItDomainException(DomainErrorKind.Validation, "unsupported_letter_type", "Tipe surat tidak tersedia.");
        var allowed = template.Fields.Where(x => x.ValueSource is not ("server" or "signatureEvidence")).Select(x => x.Key).ToHashSet();
        if (request.Fields is null || request.Fields.Any(x => !allowed.Contains(x.Key) || x.Value == null || x.Value.Length > 4000
            || x.Value.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t')))
            throw new SignItDomainException(DomainErrorKind.Validation, "invalid_draft_fields", "Field tidak sesuai template atau melebihi batas.");
        var now = clock.GetUtcNow();
        var id = Guid.NewGuid();
        var letter = LetterRequest.Create(id, $"DRAFT-{id:N}", request.TypeId, request.Title, actor, null, now);
        letter.MarkDraft();
        var json = JsonSerializer.Serialize(new SortedDictionary<string, string>(request.Fields));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        var revision = LetterRevision.Create(Guid.NewGuid(), id, 1, $"{template.TemplateId}:{template.Version}", json, hash, null, now);
        letter.SetCurrentRevision(revision.Id);
        db.LetterRequests.Add(letter);
        db.LetterRevisions.Add(revision);
        await db.SaveChangesAsync(ct);
        return Map(letter, revision);
    }

    public async Task<DraftDto> UpdateDraftAsync(Guid actor, Guid id, string? title, Dictionary<string, string> fields, CancellationToken ct)
    {
        var letter = await db.LetterRequests.SingleOrDefaultAsync(x => x.Id == id && x.SubmittedByUserId == actor, ct)
            ?? throw new SignItDomainException(DomainErrorKind.NotFound, "letter_not_found", "Surat tidak ditemukan.");
        if (letter.Status != LetterStatus.Draft)
            throw new SignItDomainException(DomainErrorKind.Conflict, "draft_not_editable", "Hanya surat dengan status draf yang dapat diubah.");

        var template = (await templates.GetAllAsync(ct)).SingleOrDefault(x => x.TypeId == letter.TypeId)
            ?? throw new SignItDomainException(DomainErrorKind.Validation, "unsupported_letter_type", "Tipe surat tidak tersedia.");
        var allowed = template.Fields.Where(x => x.ValueSource is not ("server" or "signatureEvidence")).Select(x => x.Key).ToHashSet();
        if (fields.Any(x => !allowed.Contains(x.Key) || x.Value == null || x.Value.Length > 4000))
            throw new SignItDomainException(DomainErrorKind.Validation, "invalid_draft_fields", "Field tidak sesuai template atau melebihi batas.");

        var now = clock.GetUtcNow();
        if (!string.IsNullOrWhiteSpace(title) && title.Length <= 300)
        {
            letter.UpdateDraftTitle(title);
        }

        var revision = await db.LetterRevisions.SingleAsync(x => x.Id == letter.CurrentRevisionId, ct);
        var json = JsonSerializer.Serialize(new SortedDictionary<string, string>(fields));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        revision.UpdateDraftContent(json, hash, now);
        await db.SaveChangesAsync(ct);
        return Map(letter, revision);
    }

    public async Task<DraftDto> GetAsync(Guid actor, Guid id, CancellationToken ct)
    {
        var letter = await db.LetterRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.SubmittedByUserId == actor, ct)
            ?? throw new SignItDomainException(DomainErrorKind.NotFound, "letter_not_found", "Surat tidak ditemukan.");
        var revision = await db.LetterRevisions.AsNoTracking().SingleAsync(x => x.Id == letter.CurrentRevisionId, ct);
        return Map(letter, revision);
    }

    public async Task<DraftDto> EditAsync(Guid actor, Guid id, EditDraftRequest request, string key, CancellationToken ct)
    {
        var operation = "letter.edited";
        var correlation = WorkflowIdempotency.Correlation(actor, id, operation, key);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var letter = await LockOwnerAsync(actor, id, ct);
        var replay = await WorkflowIdempotency.ReplayAsync<DraftDto>(db, id, operation, correlation, request, ct);
        if (replay != null)
        {
            var saved = await db.LetterRevisions.AsNoTracking().SingleAsync(x => x.Id == replay.RevisionId, ct);
            await transaction.CommitAsync(ct);
            return replay with { DataJson = saved.DataJson };
        }
        var old = await CheckExpectedAsync(letter, request.ExpectedVersion, request.ExpectedRevisionId, request.ExpectedContentHash, ct);
        if (letter.Status is not (LetterStatus.Draft or LetterStatus.NeedsRevision))
            throw WorkflowTaskAccess.Error(DomainErrorKind.Conflict, "letter_not_editable", "Isi surat berjalan/final tidak dapat diubah.");
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 300)
            throw WorkflowTaskAccess.Error(DomainErrorKind.Validation, "invalid_title", "Judul wajib, maksimal 300 karakter.");
        var template = (await templates.GetAllAsync(ct)).Single(x => x.TypeId == letter.TypeId);
        var allowed = template.Fields.Where(x => x.ValueSource == "user").Select(x => x.Key).ToHashSet();
        if (request.Fields == null || request.Fields.Any(x => !allowed.Contains(x.Key) || x.Value == null || x.Value.Length > 4000
            || x.Value.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t')))
            throw WorkflowTaskAccess.Error(DomainErrorKind.Validation, "invalid_draft_fields", "Field tidak sesuai schema.");
        var now = clock.GetUtcNow();
        var json = JsonSerializer.Serialize(new SortedDictionary<string, string>(request.Fields));
        var revision = LetterRevision.Create(Guid.NewGuid(), id, old.RevisionNo + 1, $"{template.TemplateId}:{template.Version}",
            json, WorkflowIdempotency.Hash(json), null, now);
        db.LetterRevisions.Add(revision);
        // Preserve completed historical tasks/evidence. Only still-open tasks are invalidated.
        var tasks = await db.WorkflowTasks.Where(x => x.RevisionId == old.Id).ToListAsync(ct);
        foreach (var task in tasks.Where(x => x.Status is WorkflowTaskStatus.Pending or WorkflowTaskStatus.Active or WorkflowTaskStatus.Deferred)) task.Supersede();
        await RevokeTaskMandatesAsync(tasks, ct);
        letter.UpdateDraft(request.Title, revision.Id);
        var result = Map(letter, revision);
        WorkflowIdempotency.Record(db, actor, id, revision.Id, operation, correlation, request, result with { DataJson = "" }, now);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task<CancelLetterDto> CancelAsync(Guid actor, Guid id, CancelLetterRequest request, string key, CancellationToken ct)
    {
        var operation = "letter.cancelled";
        var correlation = WorkflowIdempotency.Correlation(actor, id, operation, key);
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 1000)
            throw WorkflowTaskAccess.Error(DomainErrorKind.Validation, "reason_required", "Alasan pembatalan wajib, maksimal 1000 karakter.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var letter = await LockOwnerAsync(actor, id, ct);
        var replay = await WorkflowIdempotency.ReplayAsync<CancelLetterDto>(db, id, operation, correlation, request, ct);
        if (replay != null) { await transaction.CommitAsync(ct); return replay; }
        await CheckExpectedAsync(letter, request.ExpectedVersion, request.ExpectedRevisionId, request.ExpectedContentHash, ct);
        if (letter.Status is not (LetterStatus.Draft or LetterStatus.InProgress or LetterStatus.NeedsRevision))
            throw WorkflowTaskAccess.Error(DomainErrorKind.Conflict, "letter_not_cancellable", "Surat final/terminal tidak dapat dibatalkan melalui aksi ini.");
        letter.Cancel();
        var tasks = await db.WorkflowTasks.Where(x => x.RevisionId == letter.CurrentRevisionId).ToListAsync(ct);
        foreach (var task in tasks) task.Cancel();
        await RevokeTaskMandatesAsync(tasks, ct);
        var result = new CancelLetterDto(id, letter.Status, letter.RowVersion);
        WorkflowIdempotency.Record(db, actor, id, letter.CurrentRevisionId!.Value, operation, correlation, request, result, clock.GetUtcNow());
        db.AuditLogs.Add(AuditLog.Record(actor, "workflow.cancel", "LetterRequest", id, letter.CurrentRevisionId, clock.GetUtcNow(), Guid.NewGuid().ToString("N"), request.Reason));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    private async Task<LetterRequest> LockOwnerAsync(Guid actor, Guid id, CancellationToken ct)
    {
        var letter = await db.LetterRequests.FromSqlInterpolated($"SELECT * FROM letter_requests WHERE \"Id\"={id} AND \"SubmittedByUserId\"={actor} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw WorkflowTaskAccess.Error(DomainErrorKind.NotFound, "letter_not_found", "Surat tidak ditemukan.");
        await db.Entry(letter).ReloadAsync(ct);
        return letter;
    }
    private async Task<LetterRevision> CheckExpectedAsync(LetterRequest letter, Guid version, Guid revisionId, string hash, CancellationToken ct)
    {
        if (letter.RowVersion != version || letter.CurrentRevisionId != revisionId)
            throw WorkflowTaskAccess.Error(DomainErrorKind.Conflict, "stale_letter", "Versi surat berubah.");
        var revision = await db.LetterRevisions.SingleAsync(x => x.Id == revisionId, ct);
        if (revision.ContentHash != hash) throw WorkflowTaskAccess.Error(DomainErrorKind.Conflict, "stale_content", "Isi surat berubah.");
        return revision;
    }
    private async Task RevokeTaskMandatesAsync(List<WorkflowTask> tasks, CancellationToken ct)
    {
        var scopes = tasks.Select(x => WorkflowTaskAccess.DelegationScope(x.Id)).ToArray();
        foreach (var mandate in await db.Delegations.Where(x => x.Scope != null && scopes.Contains(x.Scope) && x.IsActive).ToListAsync(ct)) mandate.Revoke();
    }

    private static DraftDto Map(LetterRequest letter, LetterRevision revision) =>
        new(letter.Id, letter.TypeId, letter.Title, letter.RowVersion, revision.Id, revision.ContentHash, revision.DataJson);
}
