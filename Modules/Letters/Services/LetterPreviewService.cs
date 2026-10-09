using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Persistence;
using SignIt.Infrastructure.Storage;
using SignIt.Modules.Letters.Models;
using SignIt.Modules.Routing.Services;
using SignIt.Modules.Templates.Services;

namespace SignIt.Modules.Letters.Services;

public sealed record PreviewLetterRequest(Guid ExpectedVersion, Guid ExpectedRevisionId, string ExpectedContentHash,
    Guid OrganizationId, Guid CommitteeChairId, Guid OrganizationChairId, Guid? ResourceId);
public sealed record LetterPreviewDto(Guid JobId, Guid LetterId, Guid RevisionId, string State, string? ErrorCode,
    Guid? ReviewDocumentId, string? ReviewHash, SignatureSlot[] Slots, string? DownloadUrl);

public sealed class LetterPreviewService(AppDbContext db, RoutingService routing, TemplateCatalog templates,
    IStorageService storage, TimeProvider clock)
{
    public async Task<LetterPreviewDto> QueueAsync(Guid actor, Guid id, PreviewLetterRequest request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var letter = await db.LetterRequests.FromSqlInterpolated($"SELECT * FROM letter_requests WHERE \"Id\"={id} AND \"SubmittedByUserId\"={actor} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw Error(DomainErrorKind.NotFound, "letter_not_found", "Surat tidak ditemukan.");
        await db.Entry(letter).ReloadAsync(ct);
        if (letter.Status is not (LetterStatus.Draft or LetterStatus.NeedsRevision) || letter.RowVersion != request.ExpectedVersion || letter.CurrentRevisionId != request.ExpectedRevisionId
            || await db.WorkflowTasks.AnyAsync(x => x.RevisionId == request.ExpectedRevisionId, ct))
            throw Error(DomainErrorKind.Conflict, "stale_draft", "Draft berubah atau telah diajukan.");
        var revision = await db.LetterRevisions.SingleAsync(x => x.Id == request.ExpectedRevisionId, ct);
        if (revision.ContentHash != request.ExpectedContentHash)
            throw Error(DomainErrorKind.Conflict, "stale_content", "Isi draft telah berubah.");
        var template = (await templates.GetAllAsync(ct)).Single(x => x.TypeId == letter.TypeId);
        if (revision.TemplateVersionId != $"{template.TemplateId}:{template.Version}")
            throw Error(DomainErrorKind.Conflict, "stale_template", "Template berubah. Buat draft menggunakan schema terbaru.");
        var input = await BuildInputAsync(actor, letter, revision, template, request.OrganizationId,
            request.CommitteeChairId, request.OrganizationChairId, request.ResourceId, ct);
        var json = JsonSerializer.Serialize(input);
        var hash = Hash(Encoding.UTF8.GetBytes(json));
        var job = await db.LetterPreviewJobs.SingleOrDefaultAsync(x => x.RevisionId == revision.Id && x.InputHash == hash, ct);
        var now = clock.GetUtcNow();
        if (job == null)
        {
            // Bound storage/CPU use for repeated participant/resource changes on one draft.
            if (await db.LetterPreviewJobs.CountAsync(x => x.RevisionId == revision.Id, ct) >= 20)
                throw Error(DomainErrorKind.Conflict, "preview_limit_reached", "Batas preview revisi ini tercapai.");
            job = LetterPreviewJob.Queue(id, revision.Id, hash, json, now);
            db.LetterPreviewJobs.Add(job);
            db.AuditLogs.Add(AuditLog.Record(actor, "letter.preview_queued", "LetterRequest", id, revision.Id, now, job.Id.ToString("N"), hash));
        }
        else if (job.State == "Failed")
        {
            if (job.Attempts >= 3) throw Error(DomainErrorKind.Conflict, "preview_retry_limit", "Preview gagal berulang. Periksa konfigurasi renderer.");
            job.Retry(now);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await MapAsync(job, ct);
    }

    public async Task<LetterPreviewDto> GetAsync(Guid actor, Guid id, Guid jobId, CancellationToken ct)
    {
        await EnsureOwnerAsync(actor, id, ct);
        var job = await db.LetterPreviewJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == jobId && x.RequestId == id, ct)
            ?? throw Error(DomainErrorKind.NotFound, "preview_not_found", "Preview tidak ditemukan.");
        return await MapAsync(job, ct);
    }

    public async Task<byte[]> DownloadAsync(Guid actor, Guid id, Guid documentId, CancellationToken ct)
    {
        await EnsureOwnerAsync(actor, id, ct);
        var document = await db.Documents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == documentId
            && x.ProcessingState == "Ready"
            && (db.LetterPreviewJobs.Any(job => job.RequestId == id && job.DocumentId == documentId && job.State == "Ready")
                || (x.Kind == DocumentKind.Final && db.LetterRevisions.Any(revision => revision.Id == x.RevisionId && revision.RequestId == id))), ct)
            ?? throw Error(DomainErrorKind.NotFound, "document_not_found", "Dokumen tidak ditemukan.");
        var bytes = await storage.ReadBytesAsync(document.StorageKey, ct);
        if (bytes is null || Hash(bytes) != document.Sha256)
            throw Error(DomainErrorKind.Conflict, "review_unavailable", "Dokumen tidak tersedia atau integritasnya tidak sesuai.");
        return bytes;
    }

    public async Task<PreviewRenderInput> BuildInputAsync(Guid actor, LetterRequest letter, LetterRevision revision,
        LetterTemplateDto template, Guid organizationId, Guid committeeChairId, Guid organizationChairId, Guid? resourceId, CancellationToken ct)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(revision.DataJson) ?? [];
        var missing = template.Fields.Where(field => field.ValueSource == "user" && field.Required
            && (!values.TryGetValue(field.Key, out var value) || string.IsNullOrWhiteSpace(value))).Select(x => x.Label).ToArray();
        if (missing.Length > 0) throw Error(DomainErrorKind.Validation, "required_fields_missing", "Lengkapi field: " + string.Join(", ", missing));
        if (values.Any(x => x.Value is null || x.Value.Length > 4000 || x.Value.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t')))
            throw Error(DomainErrorKind.Validation, "invalid_draft_fields", "Isi field tidak valid.");
        var stages = await routing.ResolveAsync(actor, new(letter.TypeId, organizationId, committeeChairId, organizationChairId, resourceId), ct);
        var users = await db.Users.AsNoTracking().Where(x => stages.Select(s => s.UserId).Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var organization = await db.Organizations.AsNoTracking().SingleAsync(x => x.Id == organizationId, ct);
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in template.Fields) fields[field.Key] = field.ValueSource == "user" ? values.GetValueOrDefault(field.Key, "") : "";
        fields["nomor_surat"] = "DRAFT - nomor resmi belum terbit";
        fields["nama_kampus"] = "Politeknik Elektronika Negeri Surabaya";
        fields["nama_ormawa"] = organization.Name;
        fields["nama_ketua_pelaksana"] = users[committeeChairId].Name;
        fields["nama_penanggung_jawab"] = users[organizationChairId].Name;
        fields["nama_pembina_ormawa"] = stages.Single(x => x.PositionCode == "Pembina").Name;
        fields["nama_pembina_minat_bakat"] = stages.Single(x => x.PositionCode is "Kemahasiswaan" or "MinatBakat").Name;
        if (letter.TypeId == "peminjaman-ruangan")
        {
            var resource = await db.FacilityResources.AsNoTracking().SingleAsync(x => x.Id == resourceId, ct);
            var facility = await db.Facilities.AsNoTracking().SingleAsync(x => x.Id == resource.FacilityId, ct);
            fields["ruangan_kegiatan"] = facility.Name + " - " + resource.Code;
        }
        var (layout, assetHash) = await templates.GetRenderLayoutAsync(template, ct);
        return new(letter.TypeId, revision.TemplateVersionId!, assetHash, letter.Title, organizationId, resourceId,
            fields.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            stages.Select(stage => new RenderParticipant(stage, users[stage.UserId].NimNip)).ToArray(), layout);
    }

    private async Task EnsureOwnerAsync(Guid actor, Guid id, CancellationToken ct)
    {
        if (!await db.LetterRequests.AnyAsync(x => x.Id == id && x.SubmittedByUserId == actor, ct))
            throw Error(DomainErrorKind.NotFound, "letter_not_found", "Surat tidak ditemukan.");
    }
    private async Task<LetterPreviewDto> MapAsync(LetterPreviewJob job, CancellationToken ct)
    {
        var document = job.DocumentId.HasValue ? await db.Documents.AsNoTracking().SingleAsync(x => x.Id == job.DocumentId, ct) : null;
        return new(job.Id, job.RequestId, job.RevisionId, job.State, job.ErrorCode, job.DocumentId, document?.Sha256,
            job.SlotsJson is null ? [] : JsonSerializer.Deserialize<SignatureSlot[]>(job.SlotsJson)!,
            document is null ? null : $"/api/v1/letters/{job.RequestId}/documents/{document.Id}");
    }
    public static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static SignItDomainException Error(DomainErrorKind kind, string code, string message) => new(kind, code, message);
}
