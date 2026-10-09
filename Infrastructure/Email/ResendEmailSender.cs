using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SignIt.Modules.Authentication.Services;
using SignIt.Modules.Email.Services;

namespace SignIt.Infrastructure.Email;

public sealed class ResendEmailSender(HttpClient http, ResendOptions resend, ResetEmailOptions options)
    : IResetEmailSender, IEmailSender
{
    public async Task<EmailSendResult> SendResetAsync(Guid emailId, string recipient, string token, CancellationToken ct)
    {
        var link = options.ResetPasswordUrl + "#token=" + Uri.EscapeDataString(token);
        var safeLink = WebUtility.HtmlEncode(link);
        return await SendCoreAsync(emailId, "password-reset", recipient, "[SignIt] Reset password akun",
            $"Permintaan reset password SignIt. Buka {link}\nJika Anda tidak meminta reset, abaikan email ini.",
            $"<p>Permintaan reset password SignIt.</p><p><a href=\"{safeLink}\">Reset password</a></p>"
                + "<p>Jika Anda tidak meminta reset, abaikan email ini.</p>", ct);
    }

    public Task<EmailSendResult> SendAsync(Guid deliveryId, string recipient, string subject, string text,
        string html, CancellationToken ct)
        => SendCoreAsync(deliveryId, "delivery", recipient, subject, text, html, ct);

    private async Task<EmailSendResult> SendCoreAsync(Guid id, string keyPrefix, string recipient,
        string subject, string text, string html, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "emails");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", resend.ApiKey);
        request.Headers.Add("Idempotency-Key", $"signit/{keyPrefix}/{id:N}");
        request.Content = JsonContent.Create(new
        {
            from = options.From, to = new[] { recipient }, reply_to = options.ReplyTo,
            subject, text, html
        });

        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<SendResponse>(ct);
                return !string.IsNullOrWhiteSpace(body?.Id)
                    ? new(true, body.Id, null)
                    : new(false, null, "resend_invalid_response", true, true);
            }

            var retry = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
            var retryAfter = response.Headers.RetryAfter?.Delta;
            if (response.Headers.RetryAfter?.Date is { } retryDate)
                retryAfter = retryDate - DateTimeOffset.UtcNow;
            return new(false, null, $"resend_http_{(int)response.StatusCode}", retry,
                (int)response.StatusCode >= 500, retryAfter);
        }
        catch (HttpRequestException) { return new(false, null, "resend_network_error", true, true); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return new(false, null, "resend_timeout", true, true); }
        catch (JsonException) { return new(false, null, "resend_invalid_response", true, true); }
    }

    private sealed record SendResponse(string Id);
}
