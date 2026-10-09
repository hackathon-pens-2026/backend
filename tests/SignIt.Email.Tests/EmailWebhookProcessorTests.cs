using SignIt.Modules.Email.Models;
using SignIt.Modules.Email.Services;
using Xunit;

namespace SignIt.Email.Tests;

public sealed class EmailWebhookProcessorTests
{
    private sealed class FakeStore : IEmailEventStore
    {
        public List<EmailProviderEvent> Events { get; } = [];
        public List<EmailSuppression> Suppressions { get; } = [];
        public bool ReturnDuplicate { get; set; }

        public Task<bool> TryRecordEventAsync(EmailProviderEvent providerEvent,
            IReadOnlyList<EmailSuppression> suppressions, CancellationToken ct)
        {
            if (ReturnDuplicate) return Task.FromResult(false);
            Events.Add(providerEvent);
            Suppressions.AddRange(suppressions);
            return Task.FromResult(true);
        }
    }

    private static readonly TimeProvider Clock = TimeProvider.System;

    private static string Payload(string type, string toJson, string? bounceType = null)
    {
        var bounce = bounceType is null ? string.Empty : $",\"bounce\":{{\"type\":\"{bounceType}\"}}";
        return $"{{\"type\":\"{type}\",\"created_at\":\"2026-10-09T10:00:00Z\","
            + $"\"data\":{{\"email_id\":\"mail_1\",\"to\":{toJson}{bounce}}}}}";
    }

    [Fact]
    public async Task Delivered_IsRecordedWithoutSuppression()
    {
        var store = new FakeStore();
        var result = await new EmailWebhookProcessor(store, Clock)
            .ProcessAsync("evt_1", Payload("email.delivered", "[\"User@Example.com\"]"), CancellationToken.None);

        Assert.Equal(WebhookProcessingResult.Processed, result);
        Assert.Single(store.Events);
        Assert.Equal("email.delivered", store.Events[0].EventType);
        Assert.Equal("mail_1", store.Events[0].ProviderMessageId);
        Assert.Empty(store.Suppressions);
    }

    [Fact]
    public async Task DuplicateEvent_IsAcknowledgedWithoutEffects()
    {
        var store = new FakeStore { ReturnDuplicate = true };
        var result = await new EmailWebhookProcessor(store, Clock)
            .ProcessAsync("evt_1", Payload("email.delivered", "[\"user@example.com\"]"), CancellationToken.None);

        Assert.Equal(WebhookProcessingResult.Duplicate, result);
        Assert.Empty(store.Events);
    }

    [Fact]
    public async Task PermanentBounce_SuppressesNormalizedRecipient()
    {
        var store = new FakeStore();
        var result = await new EmailWebhookProcessor(store, Clock)
            .ProcessAsync("evt_2", Payload("email.bounced", "[\"User@Example.com\"]", "Permanent"), CancellationToken.None);

        Assert.Equal(WebhookProcessingResult.Processed, result);
        var suppression = Assert.Single(store.Suppressions);
        Assert.Equal("user@example.com", suppression.Email);
        Assert.Equal("bounced", suppression.Reason);
        Assert.Equal("resend", suppression.Source);
    }

    [Fact]
    public async Task TransientBounce_DoesNotSuppress()
    {
        var store = new FakeStore();
        var result = await new EmailWebhookProcessor(store, Clock)
            .ProcessAsync("evt_3", Payload("email.bounced", "[\"user@example.com\"]", "Transient"), CancellationToken.None);

        Assert.Equal(WebhookProcessingResult.Processed, result);
        Assert.Empty(store.Suppressions);
    }

    [Fact]
    public async Task Complaint_SuppressesSingleStringRecipient()
    {
        var store = new FakeStore();
        var result = await new EmailWebhookProcessor(store, Clock)
            .ProcessAsync("evt_4", Payload("email.complained", "\"user@example.com\""), CancellationToken.None);

        Assert.Equal(WebhookProcessingResult.Processed, result);
        var suppression = Assert.Single(store.Suppressions);
        Assert.Equal("complained", suppression.Reason);
    }

    [Fact]
    public async Task InvalidJson_IsRejected()
    {
        var store = new FakeStore();
        var result = await new EmailWebhookProcessor(store, Clock)
            .ProcessAsync("evt_5", "{not-json", CancellationToken.None);

        Assert.Equal(WebhookProcessingResult.InvalidPayload, result);
        Assert.Empty(store.Events);
    }

    [Fact]
    public async Task MissingType_IsRejected()
    {
        var store = new FakeStore();
        var result = await new EmailWebhookProcessor(store, Clock)
            .ProcessAsync("evt_6", "{\"data\":{\"email_id\":\"mail_1\"}}", CancellationToken.None);

        Assert.Equal(WebhookProcessingResult.InvalidPayload, result);
        Assert.Empty(store.Events);
    }
}
