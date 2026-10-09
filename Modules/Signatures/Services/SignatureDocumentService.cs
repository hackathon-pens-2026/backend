using Microsoft.EntityFrameworkCore;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Persistence;
using SignIt.Infrastructure.Storage;
using SignIt.Modules.Letters.Models;
using SignIt.Modules.Workflow.Models;

namespace SignIt.Modules.Signatures.Services;

// Reads immutable evidence; viewing a signed copy never changes the reviewed document.
public sealed class SignatureDocumentService(AppDbContext db, IStorageService storage,
    IQrCodeGenerator hashes, IPdfOverlayService pdf)
{
    public async Task<byte[]> RenderAsync(LetterRevision revision, CancellationToken ct)
    {
        var document = await db.Documents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == revision.FinalDocumentId
            && x.RevisionId == revision.Id && x.Kind == DocumentKind.Final && x.ProcessingState == "Ready", ct)
            ?? await db.Documents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == revision.ReviewDocumentId
                && x.Kind == DocumentKind.Review && x.ProcessingState == "Ready", ct)
            ?? throw Error("document_not_ready", "Dokumen surat belum tersedia.");
        var bytes = await storage.ReadBytesAsync(document.StorageKey, ct);
        if (bytes == null || bytes.LongLength != document.Bytes
            || !string.Equals(hashes.ComputeSha256(bytes), document.Sha256, StringComparison.OrdinalIgnoreCase))
            throw Error("document_integrity_invalid", "Dokumen surat tidak tersedia atau telah berubah.");
        if (document.Kind == DocumentKind.Final) return bytes;
        var overlays = await BuildOverlaysAsync(revision, false, ct);
        return overlays.Count == 0 ? bytes : await pdf.OverlaySignaturesAsync(bytes, overlays, null, ct);
    }

    public async Task<IReadOnlyList<PdfSignatureOverlayItem>> BuildOverlaysAsync(
        LetterRevision revision, bool requireAll, CancellationToken ct)
    {
        var participants = await db.LetterParticipants.AsNoTracking().Where(x => x.RevisionId == revision.Id)
            .OrderBy(x => x.PageIndex).ThenBy(x => x.Y).ThenBy(x => x.X).ToListAsync(ct);
        var tasks = await db.WorkflowTasks.AsNoTracking().Where(x => x.RevisionId == revision.Id).ToListAsync(ct);
        var evidences = await db.SignatureEvidences.AsNoTracking().Where(x => x.RevisionId == revision.Id).ToListAsync(ct);
        var overlays = new List<PdfSignatureOverlayItem>();
        foreach (var participant in participants)
        {
            var task = tasks.SingleOrDefault(x => x.ParticipantId == participant.Id);
            var evidence = task == null ? null : evidences.SingleOrDefault(x => x.TaskId == task.Id);
            var completed = task?.ActionType switch
            {
                WorkflowActionType.Sign => task.Status == WorkflowTaskStatus.Signed,
                WorkflowActionType.ApproveAndSign => task.Status == WorkflowTaskStatus.Approved,
                _ => false
            };
            if (!completed || evidence == null)
            {
                if (evidence != null || completed || (requireAll && participant.Required))
                    throw Error("signature_evidence_missing", "Bukti tanda tangan tidak sesuai tugas yang selesai.");
                continue;
            }
            if (task!.ActedByUserId != evidence.ActorId || task.ActedAt != evidence.SignedAt
                || !string.Equals(evidence.ContentHash, revision.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw Error("signature_evidence_invalid", "Bukti tanda tangan tidak sesuai actor atau revisi surat.");
            var qr = await db.SignatureQrs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == evidence.QrAssetId, ct);
            var image = qr == null ? null : await storage.ReadBytesAsync(qr.PrivateStorageKey, ct);
            if (qr == null || qr.OwnerUserId != evidence.ActorId || qr.Version != evidence.QrVersion
                || !string.Equals(qr.ImageSha256, evidence.QrHash, StringComparison.OrdinalIgnoreCase)
                || image == null || !string.Equals(hashes.ComputeSha256(image), evidence.QrHash, StringComparison.OrdinalIgnoreCase))
                throw Error("signature_asset_invalid", "Snapshot QR tidak tersedia atau telah berubah.");
            var name = participant.DisplayNameSnapshot;
            if (evidence.DelegatedFromUserId.HasValue)
            {
                var actorName = await db.Users.AsNoTracking().Where(x => x.Id == evidence.ActorId).Select(x => x.Name).SingleAsync(ct);
                name = $"{actorName} ({evidence.MandateDescription})";
            }
            overlays.Add(new(participant.PageIndex, participant.X, participant.Y, participant.Width,
                participant.Height, participant.Rotation, image, name, evidence.PositionSnapshot, evidence.SignedAt, true));
        }
        return overlays;
    }

    private static SignItDomainException Error(string code, string message)
        => new(DomainErrorKind.ProcessingFailed, code, message);
}
