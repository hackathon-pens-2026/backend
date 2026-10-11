using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SignIt.Infrastructure.Email;
using SignIt.Infrastructure.Llm;
using SignIt.Infrastructure.Persistence;
using SignIt.Infrastructure.Storage;
using SignIt.Modules.Authentication.Data;
using SignIt.Modules.Authentication.Services;
using SignIt.Modules.Chat.Services;
using SignIt.Modules.Email.Data;
using SignIt.Modules.Email.Services;
using SignIt.Modules.Letters.Services;
using SignIt.Modules.Rooms.Services;
using SignIt.Modules.Routing.Services;
using SignIt.Modules.Signatures.Services;
using SignIt.Modules.Templates.Services;
using SignIt.Modules.Workflow.Services;

namespace SignIt.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddSignItInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        return services
            .AddCoreInfrastructure(configuration, environment)
            .AddAuthModule(configuration)
            .AddLettersModule(configuration)
            .AddWorkflowAndRoutingModules(configuration)
            .AddChatModule(configuration)
            .AddSignaturesModule()
            .AddEmailModule(configuration, environment);
    }

    private static IServiceCollection AddCoreInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddSingleton(TimeProvider.System);

        services.AddOptions<AppDatabaseOptions>()
            .Bind(configuration.GetSection("ConnectionStrings"))
            .Validate(o => !string.IsNullOrWhiteSpace(o.DefaultConnection),
                "ConnectionStrings:DefaultConnection wajib dikonfigurasi melalui secret.")
            .ValidateOnStart();

        services.AddDbContext<AppDbContext>((sp, options) =>
        {
            options.UseNpgsql(sp.GetRequiredService<IOptions<AppDatabaseOptions>>().Value.DefaultConnection);
        });

        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection("Storage"));
        services.AddSingleton<IStorageService, LocalStorageService>();

        services.AddOptions<Email.DataProtectionOptions>()
            .Bind(configuration.GetSection("DataProtection"))
            .Validate(o => !string.IsNullOrWhiteSpace(o.KeyDirectory), "DataProtection:KeyDirectory wajib diisi.")
            .ValidateOnStart();

        services.AddDataProtection()
            .SetApplicationName("SignIt.Auth")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.GetFullPath(
                configuration["DataProtection:KeyDirectory"] ?? ".data/keys",
                environment.ContentRootPath)));

        services.AddSingleton<IResetEmailProtector, ResetEmailProtector>();

        return services;
    }

    private static IServiceCollection AddAuthModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<AuthOptions>()
            .Bind(configuration.GetSection("Auth"))
            .Validate(o => o.AccessTokenMinutes is >= 1 and <= 15 && o.SessionDays is >= 1 and <= 30
                && o.MaxFailedLoginAttempts is >= 3 and <= 20 && o.LockoutMinutes is >= 1 and <= 60
                && o.ResetTokenMinutes is >= 5 and <= 60 && o.ResetCooldownSeconds is >= 30 and <= 3600
                && o.MinimumPasswordLength is >= 12 and <= 64 && o.PasswordHashIterations is >= 210000 and <= 2000000
                && o.RateLimitPermitCount is >= 1 and <= 100 && o.RateLimitWindowSeconds is >= 1 and <= 3600
                && o.MaxActiveSessions is >= 1 and <= 50, "Konfigurasi Auth di luar batas aman.")
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<AuthOptions>>().Value);

        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection("Jwt"))
            .Validate(o => !string.IsNullOrWhiteSpace(o.Issuer) && !string.IsNullOrWhiteSpace(o.Audience)
                && !string.IsNullOrWhiteSpace(o.KeyId) && !string.IsNullOrWhiteSpace(o.PrivateKeyPem),
                "Jwt:Issuer, Audience, KeyId dan PrivateKeyPem wajib dikonfigurasi.")
            .Validate(o => JwtSigningKey.CanImportPrivateKey(o.PrivateKeyPem),
                "Jwt:PrivateKeyPem harus berupa private key RSA minimal 2048-bit.")
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<JwtOptions>>().Value);

        services.AddSingleton<JwtSigningKey>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSingleton<IPasswordService, IdentityPasswordService>();
        services.AddScoped<IAuthStore, EfAuthStore>();
        services.AddScoped<IAuthUserMapper, AuthUserMapper>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<AuthProvisioner>();

        return services;
    }

    private static IServiceCollection AddLettersModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<TemplateCatalog>();
        services.AddSingleton<ILetterTemplateRenderer, PdfSharpLetterTemplateRenderer>();
        services.AddSingleton<IDraftContentBuilder, DraftContentBuilder>();
        services.AddScoped<ILetterQueryService, LetterQueryService>();
        services.AddScoped<LettersService>();
        services.AddSingleton<ISignatureSlotValidator, PdfSignatureSlotValidator>();
        services.AddSingleton<ISubmissionWorkflowInitializer, SubmissionWorkflowInitializer>();
        services.AddSingleton<ISubmissionValidator, SubmissionValidator>();
        services.AddScoped<LetterSubmissionService>();
        services.AddScoped<LetterPreviewService>();
        services.AddScoped<LetterPreviewProcessor>();

        services.AddOptions<PreviewWorkerOptions>()
            .Bind(configuration.GetSection("Preview"))
            .Validate(o => o.PollSeconds is >= 1 and <= 60 && o.RenderTimeoutSeconds is >= 5 and <= 60
                && o.LeaseSeconds >= o.RenderTimeoutSeconds + 20 && o.LeaseSeconds <= 180,
                "Konfigurasi worker preview tidak valid.")
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<PreviewWorkerOptions>>().Value);
        services.AddHostedService<LetterPreviewWorker>();

        return services;
    }

    private static IServiceCollection AddWorkflowAndRoutingModules(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddScoped<RoutingService>();
        services.AddScoped<RoomReservationService>();

        services.AddOptions<WorkflowOptions>()
            .Bind(configuration.GetSection("Workflow"))
            .Validate(o => o.SlaDays is >= 1 and <= 30, "Workflow:SlaDays di luar batas aman.")
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<WorkflowOptions>>().Value);

        services.AddScoped<WorkflowTaskAccess>();
        services.AddScoped<IWorkflowTaskDtoMapper, WorkflowTaskDtoMapper>();
        services.AddScoped<WorkflowService>();
        services.AddScoped<WorkflowQueryService>();

        return services;
    }

    private static IServiceCollection AddChatModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<LlmOptions>()
            .Bind(configuration.GetSection("Llm"))
            .Validate(o => o.TimeoutSeconds is >= 5 and <= 120, "Llm:TimeoutSeconds harus antara 5 dan 120 detik.");
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<LlmOptions>>().Value);

        services.AddHttpClient<ILlmClient, OpenAiCompatibleLlmClient>((sp, client) =>
        {
            var llmOpts = sp.GetRequiredService<LlmOptions>();
            var baseUri = llmOpts.BaseUrl.TrimEnd('/') + "/";
            client.BaseAddress = new Uri(baseUri);
            client.Timeout = TimeSpan.FromSeconds(llmOpts.TimeoutSeconds);
        });

        services.AddScoped<IChatCandidateResolver, ChatCandidateResolver>();
        services.AddScoped<IChatExtractionProcessor, ChatExtractionProcessor>();
        services.AddSingleton<ChatTurnAdvisor>();
        services.AddScoped<LetterChatOrchestrator>();

        return services;
    }

    private static IServiceCollection AddSignaturesModule(this IServiceCollection services)
    {
        services.AddSingleton<IQrCodeGenerator, QRCoderGenerator>();
        services.AddSingleton<IPdfSignatureCardDrawer, PdfSignatureCardDrawer>();
        services.AddSingleton<IPdfOverlayService, PdfSharpOverlayService>();
        services.AddScoped<IUserSignatureQrService, UserSignatureQrService>();
        services.AddScoped<ISignatureWorkflowService, SignatureWorkflowService>();
        services.AddScoped<ILetterDocumentFinalizer, LetterDocumentFinalizer>();
        services.AddScoped<SignatureDocumentService>();
        services.AddHostedService<SignatureFinalizationWorker>();
        services.AddSingleton<IPdfDocumentInspector, PdfDocumentInspector>();
        services.AddScoped<IPublicVerificationService, PublicVerificationService>();

        return services;
    }

    private static IServiceCollection AddEmailModule(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddOptions<ResetEmailOptions>()
            .Bind(configuration.GetSection("Email"))
            .Validate(o => o.PollSeconds is >= 2 and <= 300 && o.MaxAttempts is >= 1 and <= 10
                && o.HttpTimeoutSeconds is >= 5 and <= 60 && o.DailyBudget > 0 && o.MonthlyBudget >= o.DailyBudget,
                "Konfigurasi worker email atau budget tidak valid.")
            .Validate(o => Uri.TryCreate(o.ResetPasswordUrl, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment),
                "Email:ResetPasswordUrl harus HTTPS tanpa query/fragment.")
            .Validate(o => !o.WorkerEnabled || (!string.IsNullOrWhiteSpace(o.From)
                && !string.IsNullOrWhiteSpace(o.ReplyTo)), "Email:From dan ReplyTo wajib untuk worker aktif.")
            .Validate(o => environment.IsProduction() || o.SandboxMode,
                "Lingkungan non-production wajib menggunakan Email:SandboxMode.")
            .Validate(o => Uri.TryCreate(o.AppBaseUrl, UriKind.Absolute, out var appUri)
                && (appUri.Scheme == Uri.UriSchemeHttps
                    || (environment.IsDevelopment() && appUri.Scheme == "http" && appUri.IsLoopback)),
                "Email:AppBaseUrl harus HTTPS (localhost HTTP untuk development).")
            .Validate(o => o.LetterPathTemplate.StartsWith('/') && o.LetterPathTemplate.Contains("{id}"),
                "Email:LetterPathTemplate harus diawali '/' dan memuat {id}.")
            .Validate(o => o.ReminderCooldownHours is >= 1 and <= 168 && o.MaxRemindersPerTask is >= 1 and <= 10
                && o.ReminderPollSeconds is >= 5 and <= 300, "Konfigurasi reminder email tidak valid.")
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ResetEmailOptions>>().Value);

        services.AddOptions<ResendOptions>()
            .Bind(configuration.GetSection("Resend"))
            .Validate(o => !configuration.GetValue<bool>("Email:WorkerEnabled") || !string.IsNullOrWhiteSpace(o.ApiKey),
                "Resend:ApiKey wajib untuk worker email aktif.")
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ResendOptions>>().Value);

        services.AddSingleton<IEmailWebhookVerifier, ResendWebhookVerifier>();
        services.AddScoped<IEmailEventStore, EfEmailEventStore>();
        services.AddScoped<EmailWebhookProcessor>();
        services.AddSingleton<IWorkflowEmailRenderer, WorkflowEmailRenderer>();
        services.AddScoped<WorkflowEmailService>();

        services.AddHttpClient<IResetEmailSender, ResendEmailSender>((sp, client) =>
        {
            client.BaseAddress = new Uri("https://api.resend.com/");
            client.Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<ResetEmailOptions>().HttpTimeoutSeconds);
        });
        services.AddHttpClient<IEmailSender, ResendEmailSender>((sp, client) =>
        {
            client.BaseAddress = new Uri("https://api.resend.com/");
            client.Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<ResetEmailOptions>().HttpTimeoutSeconds);
        });

        services.AddHostedService<PasswordResetEmailWorker>();
        services.AddHostedService<EmailDeliveryWorker>();
        services.AddHostedService<WorkflowReminderWorker>();

        return services;
    }
}
