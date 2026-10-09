using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SignIt.Modules.Templates.Services;

namespace SignIt.Infrastructure.Llm;

public sealed class OpenAiCompatibleLlmClient(
    HttpClient httpClient,
    LlmOptions options,
    ILogger<OpenAiCompatibleLlmClient> logger) : ILlmClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<LlmExtractionResult> ExtractFieldsAsync(
        string userMessage,
        string? currentLetterType,
        Dictionary<string, string> currentFields,
        IReadOnlyList<TemplateField> targetFields,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            logger.LogWarning("LLM ApiKey is not configured.");
            return new LlmExtractionResult(
                Success: false,
                AssistantMessage: null,
                ExtractedFields: [],
                DetectedLetterType: null,
                DetectedRoom: null,
                DetectedStartsAt: null,
                DetectedEndsAt: null,
                ErrorCode: "api_key_missing",
                ErrorMessage: "API key untuk layanan LLM belum dikonfigurasi.",
                LatencyMs: sw.ElapsedMilliseconds);
        }

        try
        {
            var sanitizedUserMessage = LlmSanitizer.Sanitize(userMessage);
            var systemPrompt = BuildSystemPrompt(currentLetterType, currentFields, targetFields);
            var payload = new
            {
                model = options.Model,
                temperature = options.Temperature,
                max_tokens = options.MaxTokens,
                response_format = new { type = "json_object" },
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = sanitizedUserMessage }
                }
            };

            var jsonContent = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
            {
                Content = jsonContent
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

            using var response = await httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                logger.LogError("LLM API returned status {StatusCode}: {ErrorBody}", response.StatusCode, errorBody);
                return new LlmExtractionResult(
                    Success: false,
                    AssistantMessage: null,
                    ExtractedFields: [],
                    DetectedLetterType: null,
                    DetectedRoom: null,
                    DetectedStartsAt: null,
                    DetectedEndsAt: null,
                    ErrorCode: $"http_{(int)response.StatusCode}",
                    ErrorMessage: $"Layanan AI mengembalikan kode kesalahan: {(int)response.StatusCode}",
                    LatencyMs: sw.ElapsedMilliseconds);
            }

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;
            var choice = root.GetProperty("choices")[0];
            var messageContent = choice.GetProperty("message").GetProperty("content").GetString();

            if (string.IsNullOrWhiteSpace(messageContent))
            {
                return new LlmExtractionResult(
                    Success: false,
                    AssistantMessage: null,
                    ExtractedFields: [],
                    DetectedLetterType: null,
                    DetectedRoom: null,
                    DetectedStartsAt: null,
                    DetectedEndsAt: null,
                    ErrorCode: "empty_response",
                    ErrorMessage: "Layanan AI tidak memberikan konten balasan.",
                    LatencyMs: sw.ElapsedMilliseconds);
            }

            return ParseLlmResponse(messageContent, sw.ElapsedMilliseconds);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "LLM API request timed out after {Timeout} seconds.", options.TimeoutSeconds);
            return new LlmExtractionResult(
                Success: false,
                AssistantMessage: null,
                ExtractedFields: [],
                DetectedLetterType: null,
                DetectedRoom: null,
                DetectedStartsAt: null,
                DetectedEndsAt: null,
                ErrorCode: "timeout",
                ErrorMessage: "Layanan AI mengalami batas waktu respons (timeout).",
                LatencyMs: sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error communicating with LLM API.");
            return new LlmExtractionResult(
                Success: false,
                AssistantMessage: null,
                ExtractedFields: [],
                DetectedLetterType: null,
                DetectedRoom: null,
                DetectedStartsAt: null,
                DetectedEndsAt: null,
                ErrorCode: "provider_exception",
                ErrorMessage: "Terjadi gangguan saat menghubungi penyedia layanan AI.",
                LatencyMs: sw.ElapsedMilliseconds);
        }
    }

    private static string BuildSystemPrompt(
        string? currentLetterType,
        Dictionary<string, string> currentFields,
        IReadOnlyList<TemplateField> targetFields)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Anda adalah asisten cerdas pembuatan surat resmi kampus Politeknik Elektronika Negeri Surabaya (PENS).");
        sb.AppendLine("Tugas Anda: mengekstrak informasi yang diberikan pengguna untuk mengisi draf surat secara akurat.");
        sb.AppendLine("Jawab SELALU dalam format JSON valid dengan schema berikut:");
        sb.AppendLine("{");
        sb.AppendLine("  \"assistant_message\": \"Kalimat ramah dan ringkas dalam Bahasa Indonesia yang mengonfirmasi data yang dicatat dan menanyakan field berikutnya.\",");
        sb.AppendLine("  \"detected_letter_type\": \"peminjaman-ruangan | peminjaman-barang | proposal | lpj | null\",");
        sb.AppendLine("  \"detected_room\": \"Nama/kode ruangan jika terdeteksi (contoh: 'Teater D4', 'HH-101') atau null\",");
        sb.AppendLine("  \"detected_starts_at\": \"ISO 8601 UTC timestamp jika ada tanggal/jam mulai atau null\",");
        sb.AppendLine("  \"detected_ends_at\": \"ISO 8601 UTC timestamp jika ada tanggal/jam selesai atau null\",");
        sb.AppendLine("  \"extracted_fields\": { \"key_field\": \"nilai yang diekstrak\" }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine($"Jenis surat saat ini: {currentLetterType ?? "belum ditentukan"}");
        sb.AppendLine("Daftar field target yang tersedia pada template ini:");
        foreach (var f in targetFields)
        {
            sb.AppendLine($"- key: '{f.Key}', label: '{f.Label}', wajib: {f.Required}, jenis: {f.Type}");
        }
        sb.AppendLine();
        sb.AppendLine("Field yang sudah terisi sebelumnya:");
        foreach (var (k, v) in currentFields)
        {
            sb.AppendLine($"- {k}: {v}");
        }
        sb.AppendLine();
        sb.AppendLine("PENTING: Jangan mengarang data yang tidak disebutkan oleh pengguna. Jika pengguna tidak menyebutkan suatu field, jangan masukkan ke 'extracted_fields'.");
        return sb.ToString();
    }

    private static LlmExtractionResult ParseLlmResponse(string rawContent, long latencyMs)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawContent);
            var root = doc.RootElement;

            var assistantMessage = root.TryGetProperty("assistant_message", out var msgProp) ? msgProp.GetString() : null;
            var detectedLetterType = root.TryGetProperty("detected_letter_type", out var typeProp) ? typeProp.GetString() : null;
            var detectedRoom = root.TryGetProperty("detected_room", out var roomProp) ? roomProp.GetString() : null;

            DateTimeOffset? startsAt = null;
            if (root.TryGetProperty("detected_starts_at", out var startProp) &&
                startProp.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(startProp.GetString(), out var sParsed))
            {
                startsAt = sParsed;
            }

            DateTimeOffset? endsAt = null;
            if (root.TryGetProperty("detected_ends_at", out var endProp) &&
                endProp.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(endProp.GetString(), out var eParsed))
            {
                endsAt = eParsed;
            }

            var extractedFields = new Dictionary<string, string>();
            if (root.TryGetProperty("extracted_fields", out var fieldsProp) &&
                fieldsProp.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in fieldsProp.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var val = prop.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            extractedFields[prop.Name] = val.Trim();
                        }
                    }
                    else if (prop.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    {
                        extractedFields[prop.Name] = prop.Value.ToString();
                    }
                }
            }

            return new LlmExtractionResult(
                Success: true,
                AssistantMessage: assistantMessage,
                ExtractedFields: extractedFields,
                DetectedLetterType: detectedLetterType,
                DetectedRoom: detectedRoom,
                DetectedStartsAt: startsAt,
                DetectedEndsAt: endsAt,
                ErrorCode: null,
                ErrorMessage: null,
                LatencyMs: latencyMs);
        }
        catch (JsonException)
        {
            return new LlmExtractionResult(
                Success: false,
                AssistantMessage: rawContent,
                ExtractedFields: [],
                DetectedLetterType: null,
                DetectedRoom: null,
                DetectedStartsAt: null,
                DetectedEndsAt: null,
                ErrorCode: "invalid_json",
                ErrorMessage: "Gagal memproses format respons dari layanan AI.",
                LatencyMs: latencyMs);
        }
    }
}
