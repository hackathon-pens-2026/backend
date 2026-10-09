using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Persistence;
using SignIt.Modules.Letters.Models;
using SignIt.Modules.Templates.Services;

namespace SignIt.Modules.Letters.Services;

public sealed record SaveDraftRequest(string TypeId, string Title, Dictionary<string, string> Fields);
public sealed record DraftDto(Guid Id, string TypeId, string Title, Guid Version, Guid RevisionId, string ContentHash, string DataJson);

public sealed class LettersService(AppDbContext db, TemplateCatalog templates, TimeProvider clock)
{
    public async Task<DraftDto> CreateAsync(Guid actor, SaveDraftRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 300)
            throw new SignItDomainException(DomainErrorKind.Validation, "invalid_title", "Judul wajib diisi, maksimal 300 karakter.");
        var template = (await templates.GetAllAsync(ct)).SingleOrDefault(x => x.TypeId == request.TypeId)
            ?? throw new SignItDomainException(DomainErrorKind.Validation, "unsupported_letter_type", "Tipe surat tidak tersedia.");
        var allowed = template.Fields.Where(x => x.ValueSource == "user").Select(x => x.Key).ToHashSet();
        if (request.Fields.Any(x => !allowed.Contains(x.Key) || x.Value == null || x.Value.Length > 4000))
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

    public async Task<DraftDto> GetAsync(Guid actor, Guid id, CancellationToken ct)
    {
        var letter = await db.LetterRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.SubmittedByUserId == actor, ct)
            ?? throw new SignItDomainException(DomainErrorKind.NotFound, "letter_not_found", "Surat tidak ditemukan.");
        var revision = await db.LetterRevisions.AsNoTracking().SingleAsync(x => x.Id == letter.CurrentRevisionId, ct);
        return Map(letter, revision);
    }

    private static DraftDto Map(LetterRequest letter, LetterRevision revision) =>
        new(letter.Id, letter.TypeId, letter.Title, letter.RowVersion, revision.Id, revision.ContentHash, revision.DataJson);
}
