using System.Net;
using System.Runtime.ExceptionServices;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using SignIt.Infrastructure;
using SignIt.Infrastructure.Configuration;
using SignIt.Infrastructure.Errors;
using SignIt.Modules.Authentication.Data;
using SignIt.Modules.Authentication.Services;

DotEnv.Load(Path.Combine(Directory.GetCurrentDirectory(), ".env"));

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);
builder.Services.AddSignItInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
    options.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
});
builder.Services.Configure<ApiBehaviorOptions>(options => options.InvalidModelStateResponseFactory = context =>
{
    var problem = ApiProblems.Create(context.HttpContext, 400, "validation_failed", "Data permintaan tidak valid.");
    problem.Extensions["errors"] = context.ModelState.Where(x => x.Value?.Errors.Count > 0)
        .ToDictionary(x => x.Key, _ => new[] { "Field wajib diisi dan harus memenuhi format/batas yang ditentukan." });
    var result = new BadRequestObjectResult(problem);
    result.ContentTypes.Add("application/problem+json");
    return result;
});
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<AuthDocumentTransformer>();
    options.AddOperationTransformer<AuthErrorsTransformer>();
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<JwtOptions, JwtSigningKey>((options, jwt, signingKey) =>
    {
        options.MapInboundClaims = false;
        options.IncludeErrorDetails = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = jwt.Issuer,
            ValidateAudience = true, ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true, IssuerSigningKey = signingKey.Key,
            RequireSignedTokens = true, RequireExpirationTime = true, ValidateLifetime = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ClockSkew = TimeSpan.Zero,
            NameClaimType = "sub"
        };
        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = context =>
            {
                // A session-store outage is a server failure, not an invalid password/token.
                if (context.Exception is NpgsqlException or DbUpdateException)
                    ExceptionDispatchInfo.Capture(context.Exception).Throw();
                return Task.CompletedTask;
            },
            OnTokenValidated = async context =>
            {
                var principal = context.Principal;
                if (!Guid.TryParse(principal?.FindFirst("sub")?.Value, out var userId)
                    || !Guid.TryParse(principal?.FindFirst("sid")?.Value, out var sessionId)
                    || !Guid.TryParse(principal?.FindFirst("sst")?.Value, out var stamp)
                    || !await context.HttpContext.RequestServices.GetRequiredService<IAuthService>()
                        .IsSessionValidAsync(userId, sessionId, stamp, context.HttpContext.RequestAborted))
                    context.Fail("Sesi tidak valid.");
            },
            OnChallenge = context =>
            {
                context.HandleResponse();
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return ApiProblems.WriteAsync(context.HttpContext, 401, "unauthorized", "Silakan login kembali.");
            },
            OnForbidden = context => ApiProblems.WriteAsync(context.HttpContext, 403, "forbidden", "Anda tidak memiliki izin untuk tindakan ini.")
        };
    });
builder.Services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder()
    .RequireAuthenticatedUser().Build());

var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
var proxies = builder.Configuration.GetSection("Proxy:KnownProxies").Get<string[]>() ?? [];
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    foreach (var proxy in proxies)
        options.KnownProxies.Add(IPAddress.TryParse(proxy, out var address) ? address
            : throw new InvalidOperationException("Proxy:KnownProxies harus berisi alamat IP proxy tepercaya."));
    options.ForwardLimit = 1;
});
if (origins.Any(origin => !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
    || origin.Contains('*') || origin != uri.GetLeftPart(UriPartial.Authority)
    || (uri.Scheme != "https" && !(builder.Environment.IsDevelopment() && uri.Scheme == "http" && uri.IsLoopback))))
    throw new InvalidOperationException("Cors:AllowedOrigins hanya menerima exact origin HTTPS (localhost HTTP untuk development).");
builder.Services.AddCors(options => options.AddPolicy("frontend", policy =>
{
    if (origins.Length > 0) policy.WithOrigins(origins).WithMethods("GET", "POST").WithHeaders("Content-Type", "Authorization");
}));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = builder.Configuration.GetValue("Auth:RateLimitWindowSeconds", 60).ToString();
        return new ValueTask(ApiProblems.WriteAsync(context.HttpContext, 429, "rate_limit_exceeded", "Terlalu banyak permintaan. Coba kembali nanti."));
    };
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue("Auth:RateLimitPermitCount", 10),
            Window = TimeSpan.FromSeconds(builder.Configuration.GetValue("Auth:RateLimitWindowSeconds", 60)),
            QueueLimit = 0, AutoReplenishment = true
        }));
    options.AddPolicy("chat", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User?.FindFirst("sub")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue("Llm:RateLimitPermitCount", 20),
            Window = TimeSpan.FromSeconds(builder.Configuration.GetValue("Llm:RateLimitWindowSeconds", 60)),
            QueueLimit = 0, AutoReplenishment = true
        }));
});

var app = builder.Build();

if (args.Length > 0 && args[0] == "--provision-demo")
{
    if (args.Length != 1 || !app.Environment.IsDevelopment())
        throw new InvalidOperationException("--provision-demo hanya tersedia pada environment Development.");
    using var scope = app.Services.CreateScope();
    var created = await DemoAccountProvisioner.ProvisionAsync(
        scope.ServiceProvider.GetRequiredService<SignIt.Infrastructure.Persistence.AppDbContext>(),
        scope.ServiceProvider.GetRequiredService<AuthProvisioner>(), app.Environment.ContentRootPath, CancellationToken.None);
    app.Logger.LogInformation("Provisioning demo selesai: {Count} akun baru. Kredensial lokal: .data/demo/credentials.json", created);
    return;
}

if (args.Length > 0 && args[0] == "--provision-auth")
{
    if (args.Length != 2) throw new InvalidOperationException("Gunakan: --provision-auth <manifest.json>");
    using var scope = app.Services.CreateScope();
    var created = await scope.ServiceProvider.GetRequiredService<AuthProvisioner>()
        .ProvisionAsync(args[1], CancellationToken.None);
    app.Logger.LogInformation("Provisioning auth selesai: {CreatedCount} akun baru; password akun lama tidak diubah.", created);
    return;
}

if (proxies.Length > 0) app.UseForwardedHeaders();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/v1/auth") || context.Request.Path.StartsWithSegments("/api/v1/me"))
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
    }
    await next(context);
});
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseHttpsRedirection();
app.UseRouting();
app.UseCors("frontend");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
if (app.Environment.IsDevelopment()) app.MapOpenApi().AllowAnonymous();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();
app.MapControllers();
app.Run();
