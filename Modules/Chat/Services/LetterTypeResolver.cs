namespace SignIt.Modules.Chat.Services;

public static class LetterTypeResolver
{
    public static string? DetectFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lower = text.ToLowerInvariant();
        if (lower.Contains("ruang") || lower.Contains("peminjaman ruangan") || lower.Contains("fasilitas"))
            return "peminjaman-ruangan";
        if (lower.Contains("barang") || lower.Contains("alat") || lower.Contains("inventaris"))
            return "peminjaman-barang";
        if (lower.Contains("proposal") || lower.Contains("kegiatan"))
            return "proposal";
        if (lower.Contains("lpj") || lower.Contains("pertanggungjawaban"))
            return "lpj";
        return null;
    }

    public static string Normalize(string input) =>
        input.Trim().ToLowerInvariant() switch
        {
            "peminjaman-ruangan" or "ruangan" or "peminjaman ruangan" or "peminjaman ruangan & fasilitas" => "peminjaman-ruangan",
            "peminjaman-barang" or "barang" or "peminjaman barang" => "peminjaman-barang",
            "proposal" => "proposal",
            "lpj" => "lpj",
            _ => input.Trim().ToLowerInvariant()
        };

    public static string GetFriendlyName(string? typeId) =>
        typeId switch
        {
            "peminjaman-ruangan" => "Peminjaman Ruangan",
            "peminjaman-barang" => "Peminjaman Barang",
            "proposal" => "Proposal Kegiatan",
            "lpj" => "Laporan Pertanggungjawaban (LPJ)",
            _ => "Surat Baru"
        };
}
