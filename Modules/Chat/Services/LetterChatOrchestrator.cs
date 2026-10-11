using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Llm;
using SignIt.Infrastructure.Persistence;
using SignIt.Modules.Authentication.Models;
using SignIt.Modules.Chat.DTOs;
using SignIt.Modules.Chat.Models;
using SignIt.Modules.Letters.Models;
using SignIt.Modules.Letters.Services;
using SignIt.Modules.Rooms.Services;
using SignIt.Modules.Templates.Services;

namespace SignIt.Modules.Chat.Services;

public sealed class LetterChatOrchestrator
{
    private readonly AppDbContext db;
    private readonly TemplateCatalog templates;
    private readonly LettersService lettersService;
    private readonly RoomReservationService roomReservationService;
    private readonly ILlmClient llmClient;
    private readonly TimeProvider clock;
    private readonly ILogger<LetterChatOrchestrator> logger;
    private readonly ChatTurnAdvisor advisor;

    private static readonly TimeSpan JakartaOffset = TimeSpan.FromHours(7);

    public LetterChatOrchestrator(
        AppDbContext db,
        TemplateCatalog templates,
        LettersService lettersService,
        RoomReservationService roomReservationService,
        ILlmClient llmClient,
        TimeProvider clock,
        ILogger<LetterChatOrchestrator> logger,
        ChatTurnAdvisor? advisor = null)
    {
        this.db = db;
        this.templates = templates;
        this.lettersService = lettersService;
        this.roomReservationService = roomReservationService;
        this.llmClient = llmClient;
        this.clock = clock;
        this.logger = logger;
        this.advisor = advisor ?? new ChatTurnAdvisor(templates);
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

        string botResponseText = string.Empty;
        string? suggestedWidget = null;
        IReadOnlyList<CandidatePersonDto>? candidates = null;
        AvailabilityDto? availability = null;
        string status = "active";
        bool fallbackAvailable = false;

        // 5. Ekstraksi LLM jika ada teks masukan dan sesi sudah memiliki tipe surat
        if (!string.IsNullOrEmpty(userText) && !string.IsNullOrWhiteSpace(session.TypeId))
        {
            var allTemplates = await templates.GetAllAsync(ct);
            var template = allTemplates.SingleOrDefault(x => x.TypeId == session.TypeId);

            if (template != null)
            {
                var llmResult = await llmClient.ExtractFieldsAsync(
                    userText,
                    session.TypeId,
                    currentFields,
                    template.Fields,
                    ct);

                var audit = AuditLog.Record(
                    actorUserId: actor,
                    action: "chat_llm_proxy",
                    entity: "ChatSession",
                    entityId: sessionId,
                    revisionId: null,
                    atUtc: clock.GetUtcNow(),
                    correlationId: sessionId.ToString(),
                    details: $"latency_ms={llmResult.LatencyMs};success={llmResult.Success};extracted_count={llmResult.ExtractedFields.Count}");
                db.AuditLogs.Add(audit);

                if (llmResult.Success)
                {
                    // Gabungkan hasil ekstraksi LLM ke field draf
                    foreach (var (k, v) in llmResult.ExtractedFields)
                    {
                        if (!string.IsNullOrWhiteSpace(v))
                        {
                            currentFields[k] = v;
                        }
                    }

                    ChatFieldSynchronizer.SynchronizeAliases(currentFields);

                    // Pengecekan tanggal dan waktu kegiatan
                    var startsAt = llmResult.DetectedStartsAt
                        ?? ParseDateTimeOffset(currentFields.GetValueOrDefault("startsAt"))
                        ?? ParseDateTimeOffset(currentFields.GetValueOrDefault("tanggal_mulai"))
                        ?? ParseDateTimeOffset(currentFields.GetValueOrDefault("start_time"));
                    var endsAt = llmResult.DetectedEndsAt
                        ?? ParseDateTimeOffset(currentFields.GetValueOrDefault("endsAt"))
                        ?? ParseDateTimeOffset(currentFields.GetValueOrDefault("tanggal_selesai"))
                        ?? ParseDateTimeOffset(currentFields.GetValueOrDefault("end_time"));

                    if (startsAt.HasValue && endsAt.HasValue)
                    {
                        if (!currentFields.ContainsKey("hari_tanggal_kegiatan") || string.IsNullOrWhiteSpace(currentFields["hari_tanggal_kegiatan"]))
                        {
                            currentFields["hari_tanggal_kegiatan"] = startsAt.Value.ToOffset(JakartaOffset).ToString("dd MMMM yyyy", new System.Globalization.CultureInfo("id-ID"));
                        }
                        if (!currentFields.ContainsKey("waktu_kegiatan") || string.IsNullOrWhiteSpace(currentFields["waktu_kegiatan"]))
                        {
                            currentFields["waktu_kegiatan"] = $"{startsAt.Value.ToOffset(JakartaOffset):HH:mm} - {endsAt.Value.ToOffset(JakartaOffset):HH:mm} WIB";
                        }
                    }

                    // Pengecekan tempat & ketersediaan ruangan
                    var roomQuery = llmResult.DetectedRoom
                        ?? currentFields.GetValueOrDefault("ruangan_kegiatan")
                        ?? currentFields.GetValueOrDefault("lokasi");

                    if (!string.IsNullOrWhiteSpace(roomQuery))
                    {
                        var matchedRoom = await FindMatchingRoomAsync(roomQuery, ct);

                        if (matchedRoom != null)
                        {
                            currentFields["ruangan_kegiatan"] = $"{matchedRoom.FacilityName} {matchedRoom.Code}".Trim();
                            currentFields["lokasi"] = currentFields["ruangan_kegiatan"];

                            if (startsAt.HasValue && endsAt.HasValue && endsAt > startsAt)
                            {
                                try
                                {
                                    availability = await roomReservationService.CheckAvailabilityAsync(
                                        matchedRoom.Id,
                                        startsAt.Value,
                                        endsAt.Value,
                                        ct);
                                }
                                catch (Exception ex)
                                {
                                    logger.LogWarning(ex, "Gagal memeriksa ketersediaan ruangan {RoomId}", matchedRoom.Id);
                                }
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(llmResult.AssistantMessage))
                    {
                        botResponseText = llmResult.AssistantMessage;
                    }
                }
                else
                {
                    logger.LogWarning("LLM Extraction failed: {ErrorCode} - {ErrorMessage}", llmResult.ErrorCode, llmResult.ErrorMessage);
                    status = "provider_unavailable";
                    fallbackAvailable = true;
                    botResponseText = "Layanan AI sedang mengalami kendala. Draf Anda tetap tersimpan dengan aman. Anda dapat melanjutkan melengkapi data melalui formulir di samping.";
                }
            }
        }

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
        var (fieldSummary, calculatedWidget) = await advisor.AnalyzeFieldsAsync(session.TypeId, currentFields, ct);
        suggestedWidget = calculatedWidget;

        // Jika bot belum memiliki kalimat balasan (misal dari direct action atau LLM tidak memberi balasan spesifik)
        if (string.IsNullOrEmpty(botResponseText))
        {
            botResponseText = advisor.BuildDeterministicBotReply(suggestedWidget, fieldSummary, availability, currentFields);
        }

        // 8. Ambil data kandidat pejabat jika widget adalah ketuaPicker atau pembinaPicker
        if (suggestedWidget == "ketuaPicker")
        {
            candidates = await GetSignerCandidatesAsync(["Ketupel", "KetuaOrganisasi"], ct);
        }
        else if (suggestedWidget == "pembinaPicker")
        {
            candidates = await GetSignerCandidatesAsync(["Pembina", "PembinaOrmawa", "DosenPembina"], ct);
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
            availability,
            draftDto,
            status,
            fallbackAvailable);
    }

    private async Task<IReadOnlyList<CandidatePersonDto>> GetSignerCandidatesAsync(
        string[] positionCodes,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return await (from a in db.Assignments.AsNoTracking()
            join u in db.Users.AsNoTracking() on a.UserId equals u.Id
            where a.IsActive && u.IsActive && a.Capability == UserCapability.Signer
                && positionCodes.Contains(a.PositionCode)
                && a.ValidFrom <= now && (a.ValidTo == null || a.ValidTo > now)
            orderby a.PositionCode, u.Name
            select new CandidatePersonDto(
                u.Id,
                u.Name,
                a.PositionCode,
                a.PositionName,
                u.NimNip != null ? $"NRP/NIP: {u.NimNip} · {a.PositionName}" : a.PositionName))
            .Distinct()
            .ToListAsync(ct);
    }

    private async Task<RoomOptionDto?> FindMatchingRoomAsync(string? query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        var q = query.Trim().ToLowerInvariant();
        var allRooms = await (from r in db.FacilityResources.AsNoTracking()
            join f in db.Facilities.AsNoTracking() on r.FacilityId equals f.Id
            where r.IsBookable
            select new RoomOptionDto(r.Id, r.Code, r.Floor, f.Id, f.Name)).ToListAsync(ct);

        return allRooms.FirstOrDefault(r =>
            q.Contains(r.Code.ToLowerInvariant()) ||
            r.Code.ToLowerInvariant().Contains(q) ||
            q.Contains(r.FacilityName.ToLowerInvariant()) ||
            r.FacilityName.ToLowerInvariant().Contains(q) ||
            $"{r.FacilityName} {r.Code}".ToLowerInvariant().Contains(q) ||
            q.Contains($"{r.FacilityName} {r.Code}".ToLowerInvariant()));
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

    private static DateTimeOffset? ParseDateTimeOffset(string? val)
    {
        if (string.IsNullOrWhiteSpace(val)) return null;
        return DateTimeOffset.TryParse(val, out var parsed) ? parsed : null;
    }
}
