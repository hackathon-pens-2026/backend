using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SignIt.Infrastructure.Llm;
using SignIt.Infrastructure.Persistence;
using SignIt.Modules.Chat.DTOs;
using SignIt.Modules.Chat.Models;
using SignIt.Modules.Chat.Services;
using SignIt.Modules.Letters.Services;
using SignIt.Modules.Rooms.Services;
using SignIt.Modules.Templates.Services;
using Xunit;

namespace SignIt.Chat.Tests;

public sealed class FakeHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "SignIt.Api";
    public string WebRootPath { get; set; } = string.Empty;
    public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = null!;
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
}

public sealed class FakeLlmClient(LlmExtractionResult resultToReturn) : ILlmClient
{
    public Task<LlmExtractionResult> ExtractFieldsAsync(
        string userMessage,
        string? currentLetterType,
        Dictionary<string, string> currentFields,
        IReadOnlyList<TemplateField> targetFields,
        CancellationToken ct)
    {
        return Task.FromResult(resultToReturn);
    }
}

public sealed class ChatDomainAndOrchestratorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public void ChatSession_Create_InitializesActiveSession()
    {
        var id = Guid.NewGuid();
        var session = ChatSession.Create(id, UserId, null, null, Now);

        Assert.Equal(id, session.Id);
        Assert.Equal(UserId, session.UserId);
        Assert.Null(session.LetterRequestId);
        Assert.Null(session.TypeId);
        Assert.Equal(ChatSessionStatus.Active, session.Status);
        Assert.Equal(Now, session.CreatedAt);
    }

    [Fact]
    public void ChatSession_SetTypeIdAndLinkDraft_UpdatesSession()
    {
        var session = ChatSession.Create(Guid.NewGuid(), UserId, null, null, Now);
        var draftId = Guid.NewGuid();
        var updateTime = Now.AddMinutes(5);

        session.SetTypeId("peminjaman-ruangan", updateTime);
        session.LinkLetterRequest(draftId, updateTime);

        Assert.Equal("peminjaman-ruangan", session.TypeId);
        Assert.Equal(draftId, session.LetterRequestId);
        Assert.Equal(updateTime, session.UpdatedAt);
    }

    [Fact]
    public void ChatMessageRecord_Create_ValidInputs_InitializesCorrectly()
    {
        var sessionId = Guid.NewGuid();
        var msg = ChatMessageRecord.Create(sessionId, "bot", "Halo!", "typePills", null, Now);

        Assert.Equal(sessionId, msg.SessionId);
        Assert.Equal("bot", msg.FromRole);
        Assert.Equal("Halo!", msg.Content);
        Assert.Equal("typePills", msg.WidgetType);
        Assert.Equal(Now, msg.CreatedAt);
    }

    [Fact]
    public async Task LlmClient_WithoutApiKey_ReturnsApiKeyMissing()
    {
        var options = new LlmOptions { ApiKey = "" };
        var client = new OpenAiCompatibleLlmClient(new HttpClient(), options, NullLogger<OpenAiCompatibleLlmClient>.Instance);

        var result = await client.ExtractFieldsAsync("tes", "peminjaman-ruangan", [], [], CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("api_key_missing", result.ErrorCode);
    }

    [Fact]
    public async Task Orchestrator_CreateSession_WithoutDraftOrType_ReturnsTypePillsQuestion()
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(dbOptions);

        var env = new FakeHostEnvironment();
        var templates = new TemplateCatalog(env);
        var lettersService = new LettersService(db, templates, TimeProvider.System);
        var roomReservationService = new RoomReservationService(db, TimeProvider.System);
        var fakeLlm = new FakeLlmClient(new LlmExtractionResult(false, null, [], null, null, null, null, "not_called", ""));
        var orchestrator = new LetterChatOrchestrator(
            db,
            templates,
            lettersService,
            roomReservationService,
            fakeLlm,
            TimeProvider.System,
            NullLogger<LetterChatOrchestrator>.Instance);

        var result = await orchestrator.CreateOrResumeSessionAsync(UserId, new CreateSessionRequest(null, null), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("typePills", result.SuggestedWidget);
        Assert.Single(result.Messages);
        Assert.Equal("Halo! Ingin membuat tipe surat apa hari ini?", result.Messages[0].Text);
    }

    [Fact]
    public async Task Orchestrator_ProcessTurn_WhenLlmFails_DegradesGracefullyWithFallback()
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(dbOptions);

        var env = new FakeHostEnvironment();
        var templates = new TemplateCatalog(env);
        var lettersService = new LettersService(db, templates, TimeProvider.System);
        var roomReservationService = new RoomReservationService(db, TimeProvider.System);

        var failingLlm = new FakeLlmClient(new LlmExtractionResult(
            Success: false,
            AssistantMessage: null,
            ExtractedFields: [],
            DetectedLetterType: null,
            DetectedRoom: null,
            DetectedStartsAt: null,
            DetectedEndsAt: null,
            ErrorCode: "timeout",
            ErrorMessage: "Timeout"));

        var orchestrator = new LetterChatOrchestrator(
            db,
            templates,
            lettersService,
            roomReservationService,
            failingLlm,
            TimeProvider.System,
            NullLogger<LetterChatOrchestrator>.Instance);

        var session = await orchestrator.CreateOrResumeSessionAsync(
            UserId,
            new CreateSessionRequest(null, "peminjaman-ruangan"),
            CancellationToken.None);

        var turnResult = await orchestrator.ProcessTurnAsync(
            UserId,
            session.SessionId,
            new SendChatMessageRequest("Nama Kegiatan: Seminar AI", null),
            CancellationToken.None);

        Assert.NotNull(turnResult);
        Assert.Equal("provider_unavailable", turnResult.Status);
        Assert.True(turnResult.FallbackAvailable);
        Assert.Contains("Layanan AI sedang mengalami kendala", turnResult.Reply.Text);
    }

    [Fact]
    public void LlmSanitizer_Masks_Sensitive_Tokens_And_Passwords()
    {
        var rawInput = "Kegiatan ini memerlukan password: Rahasia123! dan token Bearer abc.def.ghi serta key sk_1234567890123456789012";
        var sanitized = LlmSanitizer.Sanitize(rawInput);

        Assert.DoesNotContain("Rahasia123!", sanitized);
        Assert.DoesNotContain("sk_1234567890123456789012", sanitized);
        Assert.Contains("[REDACTED_PASSWORD]", sanitized);
        Assert.Contains("[REDACTED_API_KEY]", sanitized);
    }

    [Fact]
    public async Task Orchestrator_ProcessTurn_RecordsAuditLog_OnLlmProxy()
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(dbOptions);

        var env = new FakeHostEnvironment();
        var templates = new TemplateCatalog(env);
        var lettersService = new LettersService(db, templates, TimeProvider.System);
        var roomReservationService = new RoomReservationService(db, TimeProvider.System);

        var fakeLlm = new FakeLlmClient(new LlmExtractionResult(
            Success: true,
            AssistantMessage: "Data dicatat",
            ExtractedFields: new() { ["nama_kegiatan"] = "Workshop IoT" },
            DetectedLetterType: null,
            DetectedRoom: null,
            DetectedStartsAt: null,
            DetectedEndsAt: null,
            ErrorCode: null,
            ErrorMessage: null,
            LatencyMs: 42));

        var orchestrator = new LetterChatOrchestrator(
            db,
            templates,
            lettersService,
            roomReservationService,
            fakeLlm,
            TimeProvider.System,
            NullLogger<LetterChatOrchestrator>.Instance);

        var session = await orchestrator.CreateOrResumeSessionAsync(
            UserId,
            new CreateSessionRequest(null, "peminjaman-ruangan"),
            CancellationToken.None);

        await orchestrator.ProcessTurnAsync(
            UserId,
            session.SessionId,
            new SendChatMessageRequest("Nama Kegiatan: Workshop IoT", null),
            CancellationToken.None);

        var audit = await db.AuditLogs.FirstOrDefaultAsync(x => x.Action == "chat_llm_proxy");
        Assert.NotNull(audit);
        Assert.Equal(UserId, audit.ActorUserId);
        Assert.Equal("ChatSession", audit.Entity);
        Assert.Equal(session.SessionId, audit.EntityId);
        Assert.Contains("latency_ms=42", audit.Details);
        Assert.Contains("success=True", audit.Details);
    }

    [Fact]
    public async Task Orchestrator_ProcessTurn_SynchronizesFieldAliases_Bidirectionally()
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(dbOptions);

        var env = new FakeHostEnvironment();
        var templates = new TemplateCatalog(env);
        var lettersService = new LettersService(db, templates, TimeProvider.System);
        var roomReservationService = new RoomReservationService(db, TimeProvider.System);

        var fakeLlm = new FakeLlmClient(new LlmExtractionResult(
            Success: true,
            AssistantMessage: "Data dicatat",
            ExtractedFields: new() { ["nama"] = "Pelatihan Robotika Mahasiswa" },
            DetectedLetterType: null,
            DetectedRoom: null,
            DetectedStartsAt: null,
            DetectedEndsAt: null,
            ErrorCode: null,
            ErrorMessage: null,
            LatencyMs: 25));

        var orchestrator = new LetterChatOrchestrator(
            db,
            templates,
            lettersService,
            roomReservationService,
            fakeLlm,
            TimeProvider.System,
            NullLogger<LetterChatOrchestrator>.Instance);

        var session = await orchestrator.CreateOrResumeSessionAsync(
            UserId,
            new CreateSessionRequest(null, "peminjaman-ruangan"),
            CancellationToken.None);

        var turnResult = await orchestrator.ProcessTurnAsync(
            UserId,
            session.SessionId,
            new SendChatMessageRequest("Nama Kegiatan: Pelatihan Robotika Mahasiswa", new() { ["lokasi"] = "Teater D4" }),
            CancellationToken.None);

        Assert.NotNull(turnResult);
        Assert.Equal("Pelatihan Robotika Mahasiswa", turnResult.Fields["nama_kegiatan"]);
        Assert.Equal("Pelatihan Robotika Mahasiswa", turnResult.Fields["nama"]);
        Assert.Equal("Teater D4", turnResult.Fields["lokasi"]);
        Assert.Equal("Teater D4", turnResult.Fields["ruangan_kegiatan"]);
    }

    [Fact]
    public async Task Orchestrator_ProcessTurn_WhenRoomDetected_PopulatesIndonesianDateTime()
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(dbOptions);

        var env = new FakeHostEnvironment();
        var templates = new TemplateCatalog(env);
        var lettersService = new LettersService(db, templates, TimeProvider.System);
        var roomReservationService = new RoomReservationService(db, TimeProvider.System);

        var startTime = new DateTimeOffset(2026, 10, 18, 1, 0, 0, TimeSpan.Zero); // 08:00 WIB
        var endTime = new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);   // 16:00 WIB

        var fakeLlm = new FakeLlmClient(new LlmExtractionResult(
            Success: true,
            AssistantMessage: "Jadwal dan ruangan terdeteksi",
            ExtractedFields: new() { ["nama_kegiatan"] = "Workshop Cyber Security" },
            DetectedLetterType: "peminjaman-ruangan",
            DetectedRoom: "Ruang Teater",
            DetectedStartsAt: startTime,
            DetectedEndsAt: endTime,
            ErrorCode: null,
            ErrorMessage: null,
            LatencyMs: 30));

        var orchestrator = new LetterChatOrchestrator(
            db,
            templates,
            lettersService,
            roomReservationService,
            fakeLlm,
            TimeProvider.System,
            NullLogger<LetterChatOrchestrator>.Instance);

        var session = await orchestrator.CreateOrResumeSessionAsync(
            UserId,
            new CreateSessionRequest(null, "peminjaman-ruangan"),
            CancellationToken.None);

        var turnResult = await orchestrator.ProcessTurnAsync(
            UserId,
            session.SessionId,
            new SendChatMessageRequest("Ruang Teater tanggal 18 Oktober 2026 dari jam 8 sampai 16", null),
            CancellationToken.None);

        Assert.NotNull(turnResult);
        Assert.Contains("18 Oktober 2026", turnResult.Fields["hari_tanggal_kegiatan"]);
        Assert.Contains("08:00 - 16:00 WIB", turnResult.Fields["waktu_kegiatan"]);
    }
}
