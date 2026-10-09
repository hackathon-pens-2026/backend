using System.Text.Json;
using System.Text.Json.Serialization;
using SignIt.Modules.Email.Models;

namespace SignIt.Modules.Email.Services;

public enum WebhookProcessingResult { Processed, Duplicate, InvalidPayload }

public sealed class EmailWebhookProcessor(IEmailEventStore store, TimeProvider clock)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<WebhookProcessingResult> ProcessAsync(string eventId, string payload, CancellationToken ct)
    {
        ResendWebhookPayload? parsed;
        try { parsed = JsonSerializer.Deserialize<ResendWebhookPayload>(payload, JsonOptions); }
        catch (JsonException) { return WebhookProcessingResult.InvalidPayload; }
        if (parsed is null || string.IsNullOrWhiteSpace(parsed.Type) || string.IsNullOrWhiteSpace(eventId))
            return WebhookProcessingResult.InvalidPayload;

        var now = clock.GetUtcNow();
        var providerEvent = EmailProviderEvent.Record("resend", eventId, parsed.Type,
            parsed.Data?.EmailId, parsed.CreatedAt ?? now, now);
        var recorded = await store.TryRecordEventAsync(providerEvent, BuildSuppressions(parsed, now), ct);
        return recorded ? WebhookProcessingResult.Processed : WebhookProcessingResult.Duplicate;
    }

    private static IReadOnlyList<EmailSuppression> BuildSuppressions(ResendWebhookPayload payload, DateTimeOffset now)
    {
        var reason = payload.Type switch
        {
            "email.complained" => "complained",
            "email.bounced" when !IsTransientBounce(payload.Data?.Bounce) => "bounced",
            _ => null
        };
        if (reason is null) return [];
        return ReadRecipients(payload.Data?.To)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(recipient => EmailSuppression.Create(recipient, reason, "resend", now))
            .ToArray();
    }

    private static bool IsTransientBounce(BounceInfo? bounce)
        => bounce?.Type is not null && bounce.Type.Equals("Transient", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> ReadRecipients(JsonElement? to)
    {
        if (to is not { } element) return [];
        return element.ValueKind switch
        {
            JsonValueKind.String => [element.GetString() ?? string.Empty],
            JsonValueKind.Array => element.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString() ?? string.Empty)
                .ToArray(),
            _ => []
        };
    }

    private sealed record ResendWebhookPayload(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
        [property: JsonPropertyName("data")] ResendWebhookData? Data);

    private sealed record ResendWebhookData(
        [property: JsonPropertyName("email_id")] string? EmailId,
        [property: JsonPropertyName("to")] JsonElement? To,
        [property: JsonPropertyName("bounce")] BounceInfo? Bounce);

    private sealed record BounceInfo([property: JsonPropertyName("type")] string? Type);
}
