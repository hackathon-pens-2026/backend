using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PdfSharp.Pdf.IO;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Persistence;
using SignIt.Infrastructure.Storage;
using SignIt.Modules.Email.Services;
using SignIt.Modules.Letters.Models;
using SignIt.Modules.Routing.Services;
using SignIt.Modules.Templates.Services;
using SignIt.Modules.Workflow.Models;
using SignIt.Modules.Workflow.Services;

namespace SignIt.Modules.Letters.Services;

public sealed record SignatureSlot(string PositionCode, int PageIndex, double X, double Y, double Width, double Height);
public sealed record SubmitLetterRequest(Guid ExpectedVersion, Guid ExpectedRevisionId, string ExpectedContentHash,
    Guid OrganizationId, Guid CommitteeChairId, Guid OrganizationChairId, Guid? ResourceId,
    Guid ReviewDocumentId, string ExpectedReviewHash, SignatureSlot[] Slots);
public sealed record SubmissionDto(Guid LetterId, Guid RevisionId, string Number, LetterStatus Status);

public sealed class LetterSubmissionService(AppDbContext db, RoutingService routing, TemplateCatalog templates,
    IStorageService storage, TimeProvider clock, LetterPreviewService previews, WorkflowEmailService emails,
    WorkflowOptions workflow)
{
    public async Task<SubmissionDto> SubmitAsync(Guid actor, Guid id, SubmitLetterRequest request, string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 80)
            throw Error("idempotency_key_required", "Idempotency-Key wajib diisi, maksimal 80 karakter.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var letter = await db.LetterRequests.FromSqlInterpolated($"SELECT * FROM letter_requests WHERE \"Id\"={id} AND \"SubmittedByUserId\"={actor} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw new SignItDomainException(DomainErrorKind.NotFound, "letter_not_found", "Surat tidak ditemukan.");
        await db.Entry(letter).ReloadAsync(ct);
        var fingerprint = Hash(JsonSerializer.Serialize(request));
        var correlation = Hash($"{actor}:{id}:{key}");
        var previous = await db.AuditLogs.SingleOrDefaultAsync(x => x.EntityId == id && x.Action == "letter.submitted" && x.CorrelationId == correlation, ct);
        if (previous != null)
        {
            if (previous.Details != fingerprint) throw Conflict("idempotency_payload_conflict", "Key sudah digunakan untuk data berbeda.");
            await transaction.CommitAsync(ct);
            return new(id, previous.RevisionId!.Value, letter.Number, letter.Status);
        }
        if (letter.Status is not (LetterStatus.Draft or LetterStatus.NeedsRevision) || letter.RowVersion != request.ExpectedVersion || letter.CurrentRevisionId != request.ExpectedRevisionId
            || await db.WorkflowTasks.AnyAsync(x => x.RevisionId == request.ExpectedRevisionId, ct))
            throw Conflict("stale_draft", "Draft berubah atau sudah diajukan. Muat ulang surat.");
        var draft = await db.LetterRevisions.SingleAsync(x => x.Id == letter.CurrentRevisionId, ct);
        if (draft.ContentHash != request.ExpectedContentHash) throw Conflict("stale_content", "Isi draft telah berubah.");
        var template = (await templates.GetAllAsync(ct)).Single(x => x.TypeId == letter.TypeId);
        if (draft.TemplateVersionId != $"{template.TemplateId}:{template.Version}") throw Conflict("stale_template", "Template berubah. Perbarui preview.");
        var fields = JsonSerializer.Deserialize<Dictionary<string, string>>(draft.DataJson) ?? [];
        if (template.Fields.Any(x => x.ValueSource == "user" && x.Required && (!fields.TryGetValue(x.Key, out var value) || string.IsNullOrWhiteSpace(value))))
            throw Error("required_fields_missing", "Lengkapi field wajib template sebelum mengajukan.");
        var stages = await routing.ResolveAsync(actor, new(letter.TypeId, request.OrganizationId, request.CommitteeChairId, request.OrganizationChairId, request.ResourceId), ct);
        var review = await db.Documents.SingleOrDefaultAsync(x => x.Id == request.ReviewDocumentId && x.RevisionId == draft.Id
            && x.Kind == DocumentKind.Review && x.ProcessingState == "Ready" && x.MimeType == "application/pdf", ct)
            ?? throw Error("review_required", "Preview PDF untuk revisi ini belum siap.");
        var job = await db.LetterPreviewJobs.AsNoTracking().SingleOrDefaultAsync(x => x.DocumentId == review.Id
            && x.RevisionId == draft.Id && x.RequestId == id && x.State == "Ready", ct)
            ?? throw Error("generated_review_required", "Gunakan preview yang dihasilkan renderer template.");
        var renderedInput = await previews.BuildInputAsync(actor, letter, draft, template, request.OrganizationId,
            request.CommitteeChairId, request.OrganizationChairId, request.ResourceId, ct);
        if (job.InputHash != Hash(JsonSerializer.Serialize(renderedInput)))
            throw Conflict("stale_preview", "Peserta, fasilitas atau template berubah. Generate preview baru sebelum mengajukan.");
        var serverSlots = JsonSerializer.Deserialize<SignatureSlot[]>(job.SlotsJson!)!;
        if (request.Slots is null || !request.Slots.OrderBy(x => x.PositionCode).SequenceEqual(serverSlots.OrderBy(x => x.PositionCode)))
            throw Error("preview_slots_mismatch", "Slot QR harus sesuai layout preview dari server.");
        var bytes = await storage.ReadBytesAsync(review.StorageKey, ct);
        if (bytes == null || Hash(bytes) != review.Sha256 || review.Sha256 != request.ExpectedReviewHash)
            throw Conflict("review_hash_mismatch", "Preview PDF berubah atau tidak tersedia.");
        ValidateSlots(bytes, request.Slots, stages);
        var now = clock.GetUtcNow();
        var snapshot = JsonSerializer.Serialize(new { Fields = renderedInput.Fields, request.OrganizationId, request.ResourceId,
            Stages = stages, Slots = serverSlots, ReviewHash = review.Sha256, renderedInput.TemplateAssetHash,
            renderedInput.Layout.RendererVersion, PreviewJobId = job.Id, PolicyVersion = "2026-10-09" });
        var revision = LetterRevision.Create(Guid.NewGuid(), id, draft.RevisionNo + 1, draft.TemplateVersionId, snapshot, Hash(snapshot), review.Id, now);
        db.LetterRevisions.Add(revision);
        WorkflowTask? firstTask = null;
        var dueAt = now.AddDays(workflow.SlaDays);
        foreach (var stage in stages)
        {
            var slot = request.Slots.Single(x => x.PositionCode == stage.PositionCode);
            var participant = LetterParticipant.Create(Guid.NewGuid(), revision.Id,
                stage.Order == 1 ? LetterRole.Applicant : stage.Order == 2 ? LetterRole.ClosingSignatory : LetterRole.ApprovingSignatory,
                stage.PositionCode, stage.UserId, stage.Name, stage.PositionName, true, slot.PageIndex, slot.X, slot.Y, slot.Width, slot.Height);
            db.LetterParticipants.Add(participant);
            var isFirst = stage.Order == 1;
            var task = WorkflowTask.Create(Guid.NewGuid(), revision.Id, participant.Id, stage.Order,
                stage.Order <= 2 ? WorkflowActionType.Sign : WorkflowActionType.ApproveAndSign, stage.UserId,
                stage.PositionCode, isFirst ? WorkflowTaskStatus.Active : WorkflowTaskStatus.Pending,
                isFirst ? now : null, dueAt);
            if (isFirst) firstTask = task;
            db.WorkflowTasks.Add(task);
        }
        letter.SetCurrentRevision(revision.Id);
        var submitterUser = await db.Users.AsNoTracking().SingleAsync(x => x.Id == actor, ct);
        await emails.EnqueueSubmittedAsync(letter, submitterUser, ct);
        if (firstTask != null)
        {
            var firstAssignee = await db.Users.AsNoTracking().SingleAsync(x => x.Id == firstTask.AssignedUserId, ct);
            await emails.EnqueueTaskActiveAsync(firstTask, letter, firstAssignee, ct);
        }
        letter.Submit(request.OrganizationId, letter.Status == LetterStatus.NeedsRevision ? letter.Number : $"SGN-{id:N}", now);
        db.AuditLogs.Add(AuditLog.Record(actor, "letter.submitted", "LetterRequest", id, revision.Id, now, correlation, fingerprint));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(id, revision.Id, letter.Number, letter.Status);
    }

    private static void ValidateSlots(byte[] bytes, SignatureSlot[] slots, IReadOnlyList<RoutingStage> stages)
    {
        if (slots == null || slots.Length != stages.Count || slots.Select(x => x.PositionCode).Distinct().Count() != slots.Length
            || stages.Any(x => !slots.Any(s => s.PositionCode == x.PositionCode))) throw Error("invalid_slots", "Sediakan satu slot untuk setiap tahap.");
        using var stream = new MemoryStream(bytes);
        using var pdf = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        foreach (var slot in slots)
        {
            if (slot.PageIndex < 0 || slot.PageIndex >= pdf.PageCount || !double.IsFinite(slot.X) || !double.IsFinite(slot.Y)
                || !double.IsFinite(slot.Width) || !double.IsFinite(slot.Height) || slot.X < 0 || slot.Y < 0 || slot.Width < 100 || slot.Height < 100)
                throw Error("invalid_slot_bounds", "Slot QR tidak valid; ukuran minimal 100 × 100 point.");
            var page = pdf.Pages[slot.PageIndex];
            if (page.Rotate != 0 || slot.X + slot.Width > page.Width.Point || slot.Y + slot.Height > page.Height.Point)
                throw Error("invalid_slot_bounds", "Slot melewati halaman atau halaman perlu dinormalisasi rotasinya.");
            if (slots.Any(other => other != slot && other.PageIndex == slot.PageIndex && slot.X < other.X + other.Width
                && slot.X + slot.Width > other.X && slot.Y < other.Y + other.Height && slot.Y + slot.Height > other.Y))
                throw Error("overlapping_slots", "Slot tanda tangan bertumpuk.");
        }
    }
    private static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static SignItDomainException Error(string code, string message) => new(DomainErrorKind.Validation, code, message);
    private static SignItDomainException Conflict(string code, string message) => new(DomainErrorKind.Conflict, code, message);
}
