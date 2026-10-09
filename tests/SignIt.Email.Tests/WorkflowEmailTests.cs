using SignIt.Modules.Email.Models;
using SignIt.Modules.Email.Services;
using Xunit;

namespace SignIt.Email.Tests;

public sealed class WorkflowEmailTests
{
    private static readonly Guid TaskId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid LetterId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DedupKeys_AreStablePerEntityAndRecipient()
    {
        Assert.Equal($"task-active:{TaskId}:{UserId}", WorkflowEmailService.TaskActiveKey(TaskId, UserId));
        Assert.Equal($"letter-submitted:{LetterId}:{UserId}", WorkflowEmailService.SubmittedKey(LetterId, UserId));
        Assert.NotEqual(WorkflowEmailService.TaskActiveKey(TaskId, UserId),
            WorkflowEmailService.TaskActiveKey(TaskId, Guid.NewGuid()));
    }

    [Fact]
    public void ReminderKey_UsesJakartaDateAcrossUtcMidnight()
    {
        // 18:00Z = 01:00 WIB keesokan harinya.
        var utcEvening = new DateTimeOffset(2026, 11, 1, 18, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 11, 2), WorkflowEmailService.JakartaDate(utcEvening));
        Assert.Contains("20261102", WorkflowEmailService.ReminderKey(TaskId, utcEvening));

        var beforeJakartaMidnight = new DateTimeOffset(2026, 11, 1, 16, 59, 59, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 11, 1), WorkflowEmailService.JakartaDate(beforeJakartaMidnight));
    }

    [Fact]
    public void ReminderKey_DeduplicatesWithinSameJakartaDay()
    {
        var morning = new DateTimeOffset(2026, 11, 1, 1, 0, 0, TimeSpan.Zero);
        var sameDay = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal(WorkflowEmailService.ReminderKey(TaskId, morning),
            WorkflowEmailService.ReminderKey(TaskId, sameDay));
        Assert.NotEqual(WorkflowEmailService.ReminderKey(TaskId, morning),
            WorkflowEmailService.ReminderKey(TaskId, morning.AddDays(1)));
    }

    [Fact]
    public void Render_EscapesHtmlValuesButKeepsPlainText()
    {
        var (subject, text, html) = WorkflowEmailService.Render("Pengingat",
            "Surat <b>A</b> & \"B\"", "https://app.example/surat/1?x=<y>");

        Assert.Equal("Pengingat", subject);
        Assert.Contains("Surat <b>A</b> & \"B\"", text);
        Assert.Contains("&lt;b&gt;A&lt;/b&gt; &amp;", html);
        Assert.DoesNotContain("<y>", html);
        Assert.Contains("&lt;y&gt;", html);
    }

    [Fact]
    public void CancelIfPending_StopsQueuedReminder()
    {
        var delivery = EmailDelivery.Queue(Guid.NewGuid(), "task.reminder", "task-reminder:1:20261101",
            UserId, "user@example.com", "Subjek", "teks", "<p>html</p>", Now, LetterId, TaskId, isReminder: true);

        delivery.CancelIfPending("task_no_longer_active");

        Assert.Equal(EmailDeliveryStatus.Cancelled, delivery.Status);
        Assert.Equal("task_no_longer_active", delivery.LastErrorCode);
    }

    [Fact]
    public void CancelIfPending_DoesNotTouchAcceptedDelivery()
    {
        var delivery = EmailDelivery.Queue(Guid.NewGuid(), "task.reminder", "task-reminder:1:20261101",
            UserId, "user@example.com", "Subjek", "teks", "<p>html</p>", Now, LetterId, TaskId, isReminder: true);
        delivery.Accept("provider-id", Now.AddMinutes(1));

        delivery.CancelIfPending("task_no_longer_active");

        Assert.Equal(EmailDeliveryStatus.Accepted, delivery.Status);
    }
}
