using System.Text.Json;

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
            var path = Path.Combine(environment.ContentRootPath, "templates", $"{type}.json");
            if (!File.Exists(path))
                path = Path.Combine(environment.ContentRootPath, "..", "templates", $"{type}.json");
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = document.RootElement;
            var fields = root.GetProperty("fields").EnumerateArray().Select(field =>
            {
                var key = field.GetProperty("key").GetString()!;
                var group = field.GetProperty("group").GetString()!;
                var source = key.StartsWith("qr_", StringComparison.Ordinal) ? "signatureEvidence"
                    : group == "tanda_tangan" ? "participant"
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
}
