using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SignIt.Modules.Authentication.Services;
using SignIt.Modules.Authentication.Models;
using SignIt.Modules.Email.Models;
using SignIt.Infrastructure.Persistence;

namespace SignIt.Infrastructure.Email;

public sealed class PasswordResetEmailWorker(IServiceScopeFactory scopes, ResetEmailOptions options,
    TimeProvider clock, ILogger<PasswordResetEmailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.WorkerEnabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DispatchOneAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Do not log provider bodies, recipient addresses, or decrypted reset payloads.
                logger.LogError("Password reset email worker gagal ({ExceptionType}).", ex.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), clock, stoppingToken);
        }
    }

    private async Task DispatchOneAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = clock.GetUtcNow();
        PasswordResetEmail? email;
        var send = false;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            // All instances share the same conservative send budget.
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(1936287599)", ct);
            // PostgreSQL row locks plus a persisted lease coordinate multiple worker instances.
            email = await db.ResetEmails.FromSqlInterpolated($"""
                SELECT * FROM auth_password_reset_emails
                WHERE "Status" IN ('Queued', 'QuotaDeferred', 'Unknown') AND "NextAttemptAt" <= {now}
                ORDER BY "CreatedAt", "Id" LIMIT 1 FOR UPDATE SKIP LOCKED
                """).SingleOrDefaultAsync(ct);
            if (email is null) return;
            var reset = await db.ResetTokens.SingleAsync(x => x.Id == email.ResetTokenId, ct);
            var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == reset.UserId, ct);
            var normalizedRecipient = EmailSuppression.Normalize(email.Recipient);
            if (!reset.IsValid(user, now))
                email.Stop("reset_no_longer_valid", cancelled: true);
            else if (options.SandboxMode && !options.RecipientAllowlist.Contains(email.Recipient, StringComparer.OrdinalIgnoreCase))
                email.Stop("sandbox_recipient_blocked");
            else if (await db.EmailSuppressions.AnyAsync(x => x.Email == normalizedRecipient, ct))
                email.Stop("recipient_suppressed");
            else if (email.Attempts >= options.MaxAttempts || email.FirstAttemptAt <= now.AddHours(-23))
                email.Stop("retry_requires_reconciliation", unknown: email.Status == ResetEmailStatus.Unknown);
            else
            {
                var budget = await db.EmailBudgets.SingleOrDefaultAsync(ct);
                if (budget is null)
                {
                    budget = ResetEmailBudget.Create();
                    db.EmailBudgets.Add(budget);
                }
                var deferredUntil = budget.Reserve(now, options.DailyBudget, options.MonthlyBudget);
                if (deferredUntil is { } due) email.DeferQuota(due);
                else
                {
                    email.BeginAttempt(now, TimeSpan.FromSeconds(options.HttpTimeoutSeconds + 60));
                    send = true;
                }
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        if (!send) return;
        string token;
        try { token = scope.ServiceProvider.GetRequiredService<IResetEmailProtector>().Unprotect(email.ProtectedPayload); }
        catch (CryptographicException)
        {
            email.Stop("reset_payload_unavailable");
            await db.SaveChangesAsync(ct);
            return;
        }

        var result = await scope.ServiceProvider.GetRequiredService<IResetEmailSender>()
            .SendResetAsync(email.Id, email.Recipient, token, ct);
        now = clock.GetUtcNow();
        if (result.Accepted)
            email.Accept(result.ProviderId!, now);
        else if (result.Retryable && email.Attempts < options.MaxAttempts)
        {
            var backoff = TimeSpan.FromSeconds(Math.Min(900, 30 * Math.Pow(2, email.Attempts)) + Random.Shared.Next(1, 15));
            if (result.RetryAfter is { } delay && delay > backoff) backoff = delay;
            email.Retry(result.ErrorCode!, now.Add(backoff), result.Unknown);
        }
        else
            email.Stop(result.ErrorCode ?? "resend_failed", unknown: result.Unknown);

        await db.SaveChangesAsync(ct);
        if (!result.Accepted)
            logger.LogWarning("Reset email {EmailId} status {Status}: {ErrorCode}.", email.Id, email.Status, email.LastErrorCode);
    }
}
