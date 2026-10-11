using Microsoft.Extensions.Logging;
using SignIt.Infrastructure.Llm;
using SignIt.Infrastructure.Persistence;
using SignIt.Modules.Authentication.Models;
using SignIt.Modules.Letters.Models;
using SignIt.Modules.Rooms.Services;
using SignIt.Modules.Templates.Services;

namespace SignIt.Modules.Chat.Services;

public sealed class ChatExtractionProcessor : IChatExtractionProcessor
{
    private static readonly TimeSpan JakartaOffset = TimeSpan.FromHours(7);
    private static readonly System.Globalization.CultureInfo IndonesianCulture = new("id-ID");

    private readonly AppDbContext _db;
    private readonly TemplateCatalog _templates;
    private readonly ILlmClient _llmClient;
    private readonly IChatCandidateResolver _candidateResolver;
    private readonly RoomReservationService _roomReservationService;
    private readonly TimeProvider _clock;
    private readonly ILogger<ChatExtractionProcessor> _logger;

    public ChatExtractionProcessor(
        AppDbContext db,
        TemplateCatalog templates,
        ILlmClient llmClient,
        IChatCandidateResolver candidateResolver,
        RoomReservationService roomReservationService,
        TimeProvider clock,
        ILogger<ChatExtractionProcessor> logger)
    {
        _db = db;
        _templates = templates;
        _llmClient = llmClient;
        _candidateResolver = candidateResolver;
        _roomReservationService = roomReservationService;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ExtractionOutcome> ProcessExtractionAsync(
        Guid actor,
        Guid sessionId,
        string? userText,
        string? typeId,
        Dictionary<string, string> currentFields,
        CancellationToken ct)
    {
        string botResponseText = string.Empty;
        AvailabilityDto? availability = null;
        string status = "active";
        bool fallbackAvailable = false;

        if (string.IsNullOrEmpty(userText) || string.IsNullOrWhiteSpace(typeId))
        {
            return new ExtractionOutcome(botResponseText, availability, status, fallbackAvailable);
        }

        var allTemplates = await _templates.GetAllAsync(ct);
        var template = allTemplates.SingleOrDefault(x => x.TypeId == typeId);

        if (template == null)
        {
            return new ExtractionOutcome(botResponseText, availability, status, fallbackAvailable);
        }

        var llmResult = await _llmClient.ExtractFieldsAsync(
            userText,
            typeId,
            currentFields,
            template.Fields,
            ct);

        var audit = AuditLog.Record(
            actorUserId: actor,
            action: "chat_llm_proxy",
            entity: "ChatSession",
            entityId: sessionId,
            revisionId: null,
            atUtc: _clock.GetUtcNow(),
            correlationId: sessionId.ToString(),
            details: $"latency_ms={llmResult.LatencyMs};success={llmResult.Success};extracted_count={llmResult.ExtractedFields.Count}");
        _db.AuditLogs.Add(audit);

        if (!llmResult.Success)
        {
            _logger.LogWarning("LLM Extraction failed: {ErrorCode} - {ErrorMessage}", llmResult.ErrorCode, llmResult.ErrorMessage);
            status = "provider_unavailable";
            fallbackAvailable = true;
            botResponseText = "Layanan AI sedang mengalami kendala. Draf Anda tetap tersimpan dengan aman. Anda dapat melanjutkan melengkapi data melalui formulir di samping.";
            return new ExtractionOutcome(botResponseText, availability, status, fallbackAvailable);
        }

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
                currentFields["hari_tanggal_kegiatan"] = startsAt.Value.ToOffset(JakartaOffset).ToString("dd MMMM yyyy", IndonesianCulture);
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
            var matchedRoom = await _candidateResolver.FindMatchingRoomAsync(roomQuery, ct);

            if (matchedRoom != null)
            {
                currentFields["ruangan_kegiatan"] = $"{matchedRoom.FacilityName} {matchedRoom.Code}".Trim();
                currentFields["lokasi"] = currentFields["ruangan_kegiatan"];

                if (startsAt.HasValue && endsAt.HasValue && endsAt > startsAt)
                {
                    try
                    {
                        availability = await _roomReservationService.CheckAvailabilityAsync(
                            matchedRoom.Id,
                            startsAt.Value,
                            endsAt.Value,
                            ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Gagal memeriksa ketersediaan ruangan {RoomId}", matchedRoom.Id);
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(llmResult.AssistantMessage))
        {
            botResponseText = llmResult.AssistantMessage;
        }

        return new ExtractionOutcome(botResponseText, availability, status, fallbackAvailable);
    }

    private static DateTimeOffset? ParseDateTimeOffset(string? val)
    {
        if (string.IsNullOrWhiteSpace(val)) return null;
        return DateTimeOffset.TryParse(val, out var parsed) ? parsed : null;
    }
}
