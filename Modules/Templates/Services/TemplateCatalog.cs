using System.Text.Json;
using System.Security.Cryptography;

namespace SignIt.Modules.Templates.Services;

public sealed record TemplateField(string Key, string Label, string Type, bool Required, string Group, string ValueSource);
public sealed record LetterTemplateDto(string TypeId, string TemplateId, string Name, string Version,
    string SourceDocument, IReadOnlyList<TemplateField> Fields);

public sealed class TemplateCatalog(IHostEnvironment environment)
{
    private static readonly string[] Types = ["peminjaman-ruangan", "peminjaman-barang", "proposal", "lpj"];

    public async Task<IReadOnlyList<LetterTemplateDto>> GetAllAsync(CancellationToken ct)
    {
        var result = new List<LetterTemplateDto>();
        foreach (var type in Types)
        {
            var fileName = $"{type}.json";
            var path = ResolveTemplatePath(fileName)
                ?? throw new FileNotFoundException($"Template schema '{fileName}' tidak ditemukan.");

            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = document.RootElement;
            var fields = root.GetProperty("fields").EnumerateArray().Select(field =>
            {
                var key = field.GetProperty("key").GetString()!;
                var group = field.GetProperty("group").GetString()!;
                var source = key.StartsWith("qr_", StringComparison.Ordinal) ? "signatureEvidence"
                    : group == "tanda_tangan" ? "participant"
                    : key is "nama_pembina_ormawa" or "nama_ketua_pelaksana" or "nama_penanggung_jawab" or "nama_pembina_minat_bakat" ? "participant"
                    : key == "ruangan_kegiatan" ? "resource"
                    : key == "nama_ormawa" ? "organization"
                    : key == "nomor_surat" ? "server" : "user";
                return new TemplateField(key, field.GetProperty("label").GetString()!,
                    field.GetProperty("type").GetString()!, field.GetProperty("required").GetBoolean(), group, source);
            }).ToArray();
            result.Add(new LetterTemplateDto(type, root.GetProperty("template_id").GetString()!,
                root.GetProperty("name").GetString()!, root.GetProperty("version").GetString()!,
                root.GetProperty("source_document").GetString()!, fields));
        }
        return result;
    }

    public async Task<(TemplateRenderLayout Layout, string AssetHash)> GetRenderLayoutAsync(LetterTemplateDto template, CancellationToken ct)
    {
        string Resolve(string name)
        {
            if (Path.GetFileName(name) != name) throw new InvalidOperationException("Nama aset template tidak valid.");
            return ResolveTemplatePath(name)
                ?? throw new FileNotFoundException($"Aset template '{name}' tidak ditemukan.");
        }
        var layoutBytes = await File.ReadAllBytesAsync(Resolve(template.TypeId + ".layout.json"), ct);
        var layout = JsonSerializer.Deserialize<TemplateRenderLayout>(layoutBytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Layout template kosong.");
        var schema = await File.ReadAllBytesAsync(Resolve(template.TypeId + ".json"), ct);
        var source = await File.ReadAllBytesAsync(Resolve(template.SourceDocument), ct);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var bytes in new[] { schema, layoutBytes, source }) hash.AppendData(bytes);
        return (layout, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private string? ResolveTemplatePath(string name)
    {
        var current = new DirectoryInfo(environment.ContentRootPath);
        while (current != null)
        {
            var p1 = Path.Combine(current.FullName, "templates", name);
            if (File.Exists(p1)) return p1;
            var p2 = Path.Combine(current.FullName, "surat", "templates", name);
            if (File.Exists(p2)) return p2;
            current = current.Parent;
        }
        return null;
    }
}
