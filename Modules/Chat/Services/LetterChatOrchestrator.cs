using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Llm;
using SignIt.Infrastructure.Persistence;
using SignIt.Modules.Chat.DTOs;
using SignIt.Modules.Chat.Models;
using SignIt.Modules.Letters.Services;
using SignIt.Modules.Rooms.Services;
using SignIt.Modules.Templates.Services;

namespace SignIt.Modules.Chat.Services;

public sealed class LetterChatOrchestrator
{
    private readonly AppDbContext db;
    private readonly TemplateCatalog templates;
    private readonly LettersService lettersService;
    private readonly TimeProvider clock;
    private readonly ILogger<LetterChatOrchestrator> logger;
    private readonly ChatTurnAdvisor advisor;
    private readonly IChatCandidateResolver candidateResolver;
    private readonly IChatExtractionProcessor extractionProcessor;

    public LetterChatOrchestrator(
        AppDbContext db,
        TemplateCatalog templates,
        LettersService lettersService,
        RoomReservationService roomReservationService,
        ILlmClient llmClient,
        TimeProvider clock,
        ILogger<LetterChatOrchestrator> logger,
        ChatTurnAdvisor? advisor = null,
        IChatCandidateResolver? candidateResolver = null,
        IChatExtractionProcessor? extractionProcessor = null)
    {
        this.db = db;
        this.templates = templates;
        this.lettersService = lettersService;
        this.clock = clock;
        this.logger = logger;
        this.advisor = advisor ?? new ChatTurnAdvisor(templates);
        this.candidateResolver = candidateResolver ?? new ChatCandidateResolver(db, clock);
        this.extractionProcessor = extractionProcessor ?? new ChatExtractionProcessor(
            db,
            templates,
            llmClient,
            this.candidateResolver,
            roomReservationService,
            clock,
            NullLogger<ChatExtractionProcessor>.Instance);
    }

    public async Task<ChatSessionDetailDto> CreateOrResumeSessionAsync(
        Guid actor,
        CreateSessionRequest request,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        // 1. Jika request menyertakan letterRequestId yang sudah ada
        if (request.LetterRequestId.HasValue)
        {
            var letter = await db.LetterRequests.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == request.LetterRequestId.Value && x.SubmittedByUserId == actor, ct)
                ?? throw new SignItDomainException(DomainErrorKind.NotFound, "letter_not_found", "Surat draf tidak ditemukan.");

            var session = await db.ChatSessions
                .FirstOrDefaultAsync(x => x.LetterRequestId == letter.Id && x.UserId == actor, ct);

            if (session == null)
            {
                session = ChatSession.Create(Guid.NewGuid(), actor, letter.Id, letter.TypeId, now);
                db.ChatSessions.Add(session);

                var welcomeMsg = ChatMessageRecord.Create(
                    session.Id,
                    "bot",
                    $"Melanjutkan draf '{letter.Title}'. Anda dapat meminta perubahan field atau melengkapi data yang belum terisi.",
                    null,
                    null,
                    now);
                db.ChatMessages.Add(welcomeMsg);
                await db.SaveChangesAsync(ct);
            }

            return await GetSessionDetailAsync(actor, session.Id, ct);
        }

        // 2. Jika tipe surat ditentukan secara eksplisit
        if (!string.IsNullOrWhiteSpace(request.TypeId))
        {
            var normalizedType = LetterTypeResolver.Normalize(request.TypeId);
            var draftTitle = $"Draf {LetterTypeResolver.GetFriendlyName(normalizedType)}";
            var initialFields = new Dictionary<string, string>();

            var draft = await lettersService.CreateAsync(actor, new SaveDraftRequest(normalizedType, draftTitle, initialFields), ct);
            var session = ChatSession.Create(Guid.NewGuid(), actor, draft.Id, normalizedType, now);
            db.ChatSessions.Add(session);

            var welcomeText = normalizedType == "peminjaman-ruangan"
                ? "Baik, untuk Peminjaman Ruangan saya memerlukan data: nama kegiatan, jadwal (tanggal & jam), estimasi peserta, dan ruangan yang dituju."
                : $"Baik, untuk {LetterTypeResolver.GetFriendlyName(normalizedType)}, silakan jelaskan tujuan dan rincian kegiatan yang akan diajukan.";

            var welcomeMsg = ChatMessageRecord.Create(session.Id, "bot", welcomeText, null, null, now);
            db.ChatMessages.Add(welcomeMsg);
            await db.SaveChangesAsync(ct);

            return await GetSessionDetailAsync(actor, session.Id, ct);
        }

        // 3. Sesi baru tanpa draf: mulai dengan pertanyaan pembuka wajib PRD
        var newSession = ChatSession.Create(Guid.NewGuid(), actor, null, null, now);
        db.ChatSessions.Add(newSession);

        var firstQuestion = ChatMessageRecord.Create(
            newSession.Id,
            "bot",
            "Halo! Ingin membuat tipe surat apa hari ini?",
            "typePills",
            null,
            now);
        db.ChatMessages.Add(firstQuestion);
        await db.SaveChangesAsync(ct);

        return await GetSessionDetailAsync(actor, newSession.Id, ct);
    }

    public async Task<ChatSessionDetailDto> GetSessionDetailAsync(Guid actor, Guid sessionId, CancellationToken ct)
    {
        var session = await db.ChatSessions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == sessionId && x.UserId == actor, ct)
            ?? throw new SignItDomainException(DomainErrorKind.NotFound, "session_not_found", "Sesi percakapan tidak ditemukan.");

        var messages = await db.ChatMessages.AsNoTracking()
            .Where(x => x.SessionId == sessionId)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Select(x => new ChatMessageDto(x.Id, x.FromRole, x.Content, x.WidgetType, x.CreatedAt))
            .ToListAsync(ct);

        Dictionary<string, string> fields = [];
        DraftDto? draftDto = null;
        if (session.LetterRequestId.HasValue)
        {
            draftDto = await lettersService.GetAsync(actor, session.LetterRequestId.Value, ct);
            fields = ParseFieldsJson(draftDto.DataJson);
        }

        var (fieldSummary, suggestedWidget) = await advisor.AnalyzeFieldsAsync(session.TypeId, fields, ct);

        return new ChatSessionDetailDto(
            session.Id,
            session.LetterRequestId,
            session.TypeId,
            session.Status.ToString().ToLowerInvariant(),
            messages,
            fields,
            fieldSummary,
            suggestedWidget,
            draftDto);
    }

    public async Task<ChatTurnResponseDto> ProcessTurnAsync(
        Guid actor,
        Guid sessionId,
        SendChatMessageRequest request,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var session = await db.ChatSessions
            .SingleOrDefaultAsync(x => x.Id == sessionId && x.UserId == actor, ct)
            ?? throw new SignItDomainException(DomainErrorKind.NotFound, "session_not_found", "Sesi percakapan tidak ditemukan.");

        // 1. Simpan pesan pengguna jika ada teks
        var userText = request.Text?.Trim();
        if (!string.IsNullOrEmpty(userText))
        {
            var userMsg = ChatMessageRecord.Create(session.Id, "user", userText, null, null, now);
            db.ChatMessages.Add(userMsg);
        }

        // 2. Baca field draf saat ini
        Dictionary<string, string> currentFields = [];
        if (session.LetterRequestId.HasValue)
        {
            var existingDraft = await lettersService.GetAsync(actor, session.LetterRequestId.Value, ct);
            currentFields = ParseFieldsJson(existingDraft.DataJson);
        }

        // 3. Terapkan pembaruan langsung (dari aksi widget/picker jika ada)
        if (request.DirectFieldUpdates != null)
        {
            foreach (var (k, v) in request.DirectFieldUpdates)
            {
                if (!string.IsNullOrWhiteSpace(v))
                {
                    currentFields[k] = v.Trim();
                }
            }
            ChatFieldSynchronizer.SynchronizeAliases(currentFields);
        }

        // 4. Deteksi pemilihan tipe surat jika sesi belum memiliki TypeId
        if (string.IsNullOrWhiteSpace(session.TypeId))
        {
            var detectedType = LetterTypeResolver.DetectFromText(userText)
                ?? (request.DirectFieldUpdates != null && request.DirectFieldUpdates.TryGetValue("typeId", out var directType) ? directType : null);

            if (!string.IsNullOrWhiteSpace(detectedType))
            {
                var normalized = LetterTypeResolver.Normalize(detectedType);
                session.SetTypeId(normalized, now);

                if (!session.LetterRequestId.HasValue)
                {
                    var draftTitle = $"Draf {LetterTypeResolver.GetFriendlyName(normalized)}";
                    var allTmpls = await templates.GetAllAsync(ct);
                    var tmpl = allTmpls.SingleOrDefault(x => x.TypeId == normalized);
                    var allowed = tmpl?.Fields.Where(x => x.ValueSource is not ("server" or "signatureEvidence")).Select(x => x.Key).ToHashSet() ?? [];
                    var validInitialFields = currentFields.Where(kv => allowed.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
                    var createdDraft = await lettersService.CreateAsync(actor, new SaveDraftRequest(normalized, draftTitle, validInitialFields), ct);
                    session.LinkLetterRequest(createdDraft.Id, now);
                }
            }
        }

        // 5. Ekstraksi LLM, enrich Jakarta time, & ketersediaan ruangan via ChatExtractionProcessor
        var extractionOutcome = await extractionProcessor.ProcessExtractionAsync(
            actor,
            sessionId,
            userText,
            session.TypeId,
            currentFields,
            ct);

        // 6. Sinkronisasi draf ke database
        DraftDto? draftDto = null;
        if (session.LetterRequestId.HasValue)
        {
            var draftTitle = currentFields.GetValueOrDefault("nama_kegiatan")
                ?? currentFields.GetValueOrDefault("perihal")
                ?? $"Draf {LetterTypeResolver.GetFriendlyName(session.TypeId)}";

            draftDto = await lettersService.UpdateDraftAsync(
                actor,
                session.LetterRequestId.Value,
                draftTitle,
                currentFields,
                ct);
        }

        // 7. Analisis field yang masih kurang & tentukan widget rekomendasi deterministik
        var (fieldSummary, suggestedWidget) = await advisor.AnalyzeFieldsAsync(session.TypeId, currentFields, ct);

        // Tentukan kalimat balasan bot
        var botResponseText = extractionOutcome.BotResponseText;
        if (string.IsNullOrEmpty(botResponseText))
        {
            botResponseText = advisor.BuildDeterministicBotReply(suggestedWidget, fieldSummary, extractionOutcome.Availability, currentFields);
        }

        // 8. Ambil data kandidat pejabat jika widget adalah ketuaPicker atau pembinaPicker
        IReadOnlyList<CandidatePersonDto>? candidates = null;
        if (suggestedWidget == "ketuaPicker")
        {
            candidates = await candidateResolver.GetSignerCandidatesAsync(["Ketupel", "KetuaOrganisasi"], ct);
        }
        else if (suggestedWidget == "pembinaPicker")
        {
            candidates = await candidateResolver.GetSignerCandidatesAsync(["Pembina", "PembinaOrmawa", "DosenPembina"], ct);
        }

        // 9. Simpan pesan bot ke database
        var botMessageRecord = ChatMessageRecord.Create(
            session.Id,
            "bot",
            botResponseText,
            suggestedWidget,
            null,
            clock.GetUtcNow());
        db.ChatMessages.Add(botMessageRecord);

        session.Touch(now);
        await db.SaveChangesAsync(ct);

        var replyDto = new ChatMessageDto(
            botMessageRecord.Id,
            botMessageRecord.FromRole,
            botMessageRecord.Content,
            botMessageRecord.WidgetType,
            botMessageRecord.CreatedAt);

        return new ChatTurnResponseDto(
            session.Id,
            session.LetterRequestId,
            session.TypeId,
            replyDto,
            currentFields,
            fieldSummary,
            suggestedWidget,
            candidates,
            extractionOutcome.Availability,
            draftDto,
            extractionOutcome.Status,
            extractionOutcome.FallbackAvailable);
    }

    private static Dictionary<string, string> ParseFieldsJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
