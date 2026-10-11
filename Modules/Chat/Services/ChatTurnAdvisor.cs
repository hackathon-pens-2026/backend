using SignIt.Modules.Chat.DTOs;
using SignIt.Modules.Rooms.Services;
using SignIt.Modules.Templates.Services;

namespace SignIt.Modules.Chat.Services;

public sealed class ChatTurnAdvisor(TemplateCatalog templates)
{
    public async Task<(FieldSummaryDto Summary, string? SuggestedWidget)> AnalyzeFieldsAsync(
        string? typeId,
        Dictionary<string, string> fields,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(typeId))
        {
            return (new FieldSummaryDto(0, 0, []), "typePills");
        }

        var allTemplates = await templates.GetAllAsync(ct);
        var template = allTemplates.SingleOrDefault(x => x.TypeId == typeId);
        if (template == null)
        {
            return (new FieldSummaryDto(0, 0, []), null);
        }

        var requiredFillable = template.Fields
            .Where(f => f.Required && f.ValueSource is not ("server" or "signatureEvidence"))
            .ToList();

        var total = requiredFillable.Count;
        var missingKeys = requiredFillable
            .Where(f => !fields.ContainsKey(f.Key) || string.IsNullOrWhiteSpace(fields[f.Key]))
            .Select(f => f.Key)
            .ToList();

        var filled = total - missingKeys.Count;
        var summary = new FieldSummaryDto(total, filled, missingKeys);

        if (missingKeys.Count == 0)
        {
            return (summary, "pdf");
        }

        // Tentukan widget berdasarkan field prioritas yang belum terisi
        string? nextWidget = null;
        if (missingKeys.Any(k => k.Contains("ruangan") || k.Contains("lokasi")))
        {
            nextWidget = "availability";
        }
        else if (missingKeys.Any(k => k.Contains("ketua") || k.Contains("pemohon_2")))
        {
            nextWidget = "ketuaPicker";
        }
        else if (missingKeys.Any(k => k.Contains("pembina") || k.Contains("dosen")))
        {
            nextWidget = "pembinaPicker";
        }
        else if (missingKeys.Any(k => k.Contains("rundown") || k.Contains("lampiran")))
        {
            nextWidget = "upload";
        }

        return (summary, nextWidget);
    }

    public string BuildDeterministicBotReply(
        string? widget,
        FieldSummaryDto summary,
        AvailabilityDto? availability,
        Dictionary<string, string> fields)
    {
        if (widget == "pdf")
        {
            return "Semua field wajib sudah lengkap terisi! Tekan 'Generate Draf Surat' di panel kanan untuk memeriksa pratinjau dokumen.";
        }

        if (widget == "ketuaPicker")
        {
            var prefix = availability?.IsAvailable == true ? "Ruangan tersedia! " : string.Empty;
            return $"{prefix}Silakan pilih Ketua Pelaksana yang terdaftar di sistem:";
        }

        if (widget == "pembinaPicker")
        {
            return "Selanjutnya, silakan pilih Dosen Pembina yang akan mengetahui permohonan surat ini:";
        }

        if (widget == "availability")
        {
            return "Silakan tentukan tempat (ruangan) dan jadwal waktu kegiatan peminjaman:";
        }

        if (widget == "upload")
        {
            return "Tinggal satu lampiran: silakan unggah file dokumen pendukung/rundown acara (PDF/DOCX).";
        }

        if (widget == "typePills")
        {
            return "Halo! Ingin membuat tipe surat apa hari ini?";
        }

        if (summary.MissingRequiredKeys.Count > 0)
        {
            return $"Data telah dicatat. Masih ada {summary.MissingRequiredKeys.Count} field wajib yang perlu dilengkapi: {string.Join(", ", summary.MissingRequiredKeys)}.";
        }

        return "Data draf berhasil diperbarui.";
    }
}
