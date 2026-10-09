using System.Net;
using Microsoft.EntityFrameworkCore;
using SignIt.Infrastructure.Email;
using SignIt.Infrastructure.Persistence;
using SignIt.Modules.Authentication.Models;
using SignIt.Modules.Email.Models;
using SignIt.Modules.Letters.Models;
using SignIt.Modules.Workflow.Models;

namespace SignIt.Modules.Email.Services;

// Outbox notifikasi workflow: ditulis atomik bersama transisi workflow oleh pemanggil.
public sealed class WorkflowEmailService(AppDbContext db, ResetEmailOptions options, TimeProvider clock)
{
    private static readonly TimeSpan JakartaOffset = TimeSpan.FromHours(7);
    private const string TemplateVersion = "v1";

    public static string SubmittedKey(Guid letterId, Guid userId) => $"letter-submitted:{letterId}:{userId}";
    public static string TaskActiveKey(Guid taskId, Guid userId) => $"task-active:{taskId}:{userId}";
    public static string RevisionKey(Guid revisionId, Guid userId) => $"letter-revision:{revisionId}:{userId}";
    public static string RejectedKey(Guid letterId, Guid userId) => $"letter-rejected:{letterId}:{userId}";
    public static string CompletedKey(Guid letterId, Guid userId) => $"letter-completed:{letterId}:{userId}";
    public static string ReminderKey(Guid taskId, DateTimeOffset now) => $"task-reminder:{taskId}:{JakartaDate(now):yyyyMMdd}";

    public static DateOnly JakartaDate(DateTimeOffset now) => DateOnly.FromDateTime(now.ToOffset(JakartaOffset).DateTime);

    public Task EnqueueSubmittedAsync(LetterRequest letter, User recipient, CancellationToken ct)
    {
        var link = LetterLink(letter.Id);
        var (subject, text, html) = Render("Surat berhasil diajukan",
            $"Surat {letter.Number} ({letter.Title}) telah diajukan dan menunggu persetujuan.", link);
        return EnqueueAsync(EmailDelivery.Queue(Guid.NewGuid(), "letter.submitted",
            SubmittedKey(letter.Id, recipient.Id), recipient.Id, recipient.Email, subject, text, html,
            clock.GetUtcNow(), letter.Id), ct);
    }

    public Task EnqueueTaskActiveAsync(WorkflowTask task, LetterRequest letter, User recipient, CancellationToken ct)
    {
        var link = LetterLink(letter.Id);
        var (subject, text, html) = Render($"Surat {letter.Number}: menunggu persetujuan Anda",
            $"Surat {letter.Number} ({letter.Title}) menunggu tindakan Anda.", link);
        return EnqueueAsync(EmailDelivery.Queue(Guid.NewGuid(), "task.activated",
            TaskActiveKey(task.Id, recipient.Id), recipient.Id, recipient.Email, subject, text, html,
            clock.GetUtcNow(), letter.Id, task.Id), ct);
    }

    public Task EnqueueRevisionRequestedAsync(LetterRequest letter, Guid revisionId, User recipient,
        string comment, CancellationToken ct)
    {
        var link = LetterLink(letter.Id);
        var (subject, text, html) = Render($"Surat {letter.Number}: perlu revisi",
            $"Surat {letter.Number} memerlukan revisi. Catatan: {comment}", link);
        return EnqueueAsync(EmailDelivery.Queue(Guid.NewGuid(), "letter.revision_requested",
            RevisionKey(revisionId, recipient.Id), recipient.Id, recipient.Email, subject, text, html,
            clock.GetUtcNow(), letter.Id), ct);
    }

    public Task EnqueueRejectedAsync(LetterRequest letter, User recipient, string comment, CancellationToken ct)
    {
        var link = LetterLink(letter.Id);
        var (subject, text, html) = Render($"Surat {letter.Number}: ditolak",
            $"Surat {letter.Number} ditolak. Alasan: {comment}", link);
        return EnqueueAsync(EmailDelivery.Queue(Guid.NewGuid(), "letter.rejected",
            RejectedKey(letter.Id, recipient.Id), recipient.Id, recipient.Email, subject, text, html,
            clock.GetUtcNow(), letter.Id), ct);
    }

    public Task EnqueueCompletedAsync(LetterRequest letter, User recipient, CancellationToken ct)
    {
        var link = LetterLink(letter.Id);
        var (subject, text, html) = Render($"Surat {letter.Number}: selesai",
            $"Surat {letter.Number} ({letter.Title}) telah selesai dan dapat diunduh melalui aplikasi.", link);
        return EnqueueAsync(EmailDelivery.Queue(Guid.NewGuid(), "letter.completed",
            CompletedKey(letter.Id, recipient.Id), recipient.Id, recipient.Email, subject, text, html,
            clock.GetUtcNow(), letter.Id), ct);
    }

    // Reminder SLA: hanya tugas Active yang overdue; dedup per tugas per hari Asia/Jakarta dan dibatasi jumlahnya.
    public async Task<int> DispatchDueRemindersAsync(DateTimeOffset now, CancellationToken ct)
    {
        var candidates = await (from task in db.WorkflowTasks
            join revision in db.LetterRevisions on task.RevisionId equals revision.Id
            join letter in db.LetterRequests on revision.RequestId equals letter.Id
            join user in db.Users on task.AssignedUserId equals user.Id
            where task.Status == WorkflowTaskStatus.Active
                && task.DueAt != null && task.DueAt <= now
                && user.IsActive
                && (letter.Status == LetterStatus.InProgress || letter.Status == LetterStatus.AwaitingResourceResolution)
            orderby task.DueAt
            select new { Task = task, Letter = letter, User = user }).Take(50).ToListAsync(ct);

        var queued = 0;
        foreach (var candidate in candidates)
        {
            var reminderCount = await db.EmailDeliveries.CountAsync(x =>
                x.RelatedTaskId == candidate.Task.Id && x.IsReminder, ct);
            if (reminderCount >= options.MaxRemindersPerTask) continue;

            var lastReminderAt = await db.EmailDeliveries
                .Where(x => x.RelatedTaskId == candidate.Task.Id && x.IsReminder)
                .MaxAsync(x => (DateTimeOffset?)x.CreatedAt, ct);
            if (lastReminderAt is { } last && last > now.AddHours(-options.ReminderCooldownHours)) continue;

            var link = LetterLink(candidate.Letter.Id);
            var (subject, text, html) = Render($"Pengingat: surat {candidate.Letter.Number} menunggu tindakan Anda",
                $"Tugas Anda pada surat {candidate.Letter.Number} telah melewati batas waktu. Mohon segera ditindaklanjuti.", link);
            var delivery = EmailDelivery.Queue(Guid.NewGuid(), "task.reminder",
                ReminderKey(candidate.Task.Id, now), candidate.User.Id, candidate.User.Email, subject, text, html,
                now, candidate.Letter.Id, candidate.Task.Id, isReminder: true);
            if (await EnqueueAsync(delivery, ct)) queued++;
        }
        if (queued > 0) await db.SaveChangesAsync(ct);
        return queued;
    }

    public async Task<int> CancelQueuedRemindersAsync(Guid taskId, CancellationToken ct)
    {
        var pending = await db.EmailDeliveries
            .Where(x => x.RelatedTaskId == taskId && x.IsReminder
                && (x.Status == EmailDeliveryStatus.Queued
                    || x.Status == EmailDeliveryStatus.QuotaDeferred
                    || x.Status == EmailDeliveryStatus.Unknown))
            .ToListAsync(ct);
        foreach (var delivery in pending) delivery.CancelIfPending("task_no_longer_active");
        return pending.Count;
    }

    private async Task<bool> EnqueueAsync(EmailDelivery delivery, CancellationToken ct)
    {
        var exists = await db.EmailDeliveries.AnyAsync(x => x.DeduplicationKey == delivery.DeduplicationKey, ct);
        if (exists) return false;
        db.EmailDeliveries.Add(delivery);
        return true;
    }

    private string LetterLink(Guid letterId)
        => options.AppBaseUrl.TrimEnd('/') + options.LetterPathTemplate.Replace("{id}", letterId.ToString());

    public static (string Subject, string Text, string Html) Render(string subject, string message, string link)
    {
        var safeMessage = WebUtility.HtmlEncode(message);
        var safeLink = WebUtility.HtmlEncode(link);
        var text = $"{message}\nTinjau surat: {link}";
        var html = $"<p>{safeMessage}</p><p><a href=\"{safeLink}\">Tinjau surat</a></p>";
        return (subject, text, html);
    }
}
