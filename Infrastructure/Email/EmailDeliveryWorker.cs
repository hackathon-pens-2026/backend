using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SignIt.Infrastructure.Persistence;
using SignIt.Modules.Email.Models;
using SignIt.Modules.Email.Services;
using SignIt.Modules.Workflow.Models;

namespace SignIt.Infrastructure.Email;

// Worker outbox email generik (notifikasi workflow + reminder) dengan dedup, retry, dan suppression.
public sealed class EmailDeliveryWorker(IServiceScopeFactory scopes, ResetEmailOptions options,
    TimeProvider clock, ILogger<EmailDeliveryWorker> logger) : BackgroundService
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
                logger.LogError("Email delivery worker gagal ({ExceptionType}).", ex.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), clock, stoppingToken);
        }
    }

    private async Task DispatchOneAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = clock.GetUtcNow();
        EmailDelivery? delivery;
        var send = false;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            delivery = await db.EmailDeliveries.FromSqlInterpolated($"""
                SELECT * FROM email_deliveries
                WHERE "Status" IN ('Queued', 'QuotaDeferred', 'Unknown') AND "NextAttemptAt" <= {now}
                ORDER BY "CreatedAt", "Id" LIMIT 1 FOR UPDATE SKIP LOCKED
                """).SingleOrDefaultAsync(ct);
            if (delivery is null) return;

            var recipient = EmailSuppression.Normalize(delivery.RecipientEmailSnapshot);
            if (await db.EmailSuppressions.AnyAsync(x => x.Email == recipient, ct))
                delivery.Stop("recipient_suppressed", suppressed: true);
            else if (delivery.IsReminder && delivery.RelatedTaskId is { } taskId
                && !await db.WorkflowTasks.AnyAsync(x => x.Id == taskId && x.Status == WorkflowTaskStatus.Active, ct))
                // Reminder berhenti saat tugas tidak lagi aktif.
                delivery.Stop("task_no_longer_active", cancelled: true);
            else if (delivery.Attempts >= options.MaxAttempts || delivery.FirstAttemptAt <= now.AddHours(-23))
                delivery.Stop("retry_requires_reconciliation", unknown: delivery.Status == EmailDeliveryStatus.Unknown);
            else
            {
                delivery.BeginAttempt(now, TimeSpan.FromSeconds(options.HttpTimeoutSeconds + 60));
                send = true;
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        if (!send) return;
        var result = await scope.ServiceProvider.GetRequiredService<IEmailSender>()
            .SendAsync(delivery.Id, delivery.RecipientEmailSnapshot, delivery.Subject, delivery.BodyText,
                delivery.BodyHtml, ct);
        now = clock.GetUtcNow();
        if (result.Accepted)
            delivery.Accept(result.ProviderId!, now);
        else if (result.Retryable && delivery.Attempts < options.MaxAttempts)
        {
            var backoff = TimeSpan.FromSeconds(Math.Min(900, 30 * Math.Pow(2, delivery.Attempts)) + Random.Shared.Next(1, 15));
            if (result.RetryAfter is { } delay && delay > backoff) backoff = delay;
            delivery.Retry(result.ErrorCode!, now.Add(backoff), result.Unknown);
        }
        else
            delivery.Stop(result.ErrorCode ?? "resend_failed", unknown: result.Unknown);

        await db.SaveChangesAsync(ct);
        if (!result.Accepted)
            logger.LogWarning("Email delivery {DeliveryId} status {Status}: {ErrorCode}.",
                delivery.Id, delivery.Status, delivery.LastErrorCode);
    }
}
