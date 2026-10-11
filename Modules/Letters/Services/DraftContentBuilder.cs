using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SignIt.Infrastructure.Errors;
using SignIt.Modules.Templates.Services;

namespace SignIt.Modules.Letters.Services;

public sealed class DraftContentBuilder : IDraftContentBuilder
{
    public DraftContentResult BuildForCreation(LetterTemplateDto template, Dictionary<string, string>? fields)
    {
        var allowed = template.Fields
            .Where(x => x.ValueSource is not ("server" or "signatureEvidence"))
            .Select(x => x.Key)
            .ToHashSet();

        if (fields is null || fields.Any(x => !allowed.Contains(x.Key) || x.Value == null || x.Value.Length > 4000
            || x.Value.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t')))
        {
            throw new SignItDomainException(DomainErrorKind.Validation, "invalid_draft_fields", "Field tidak sesuai template atau melebihi batas.");
        }

        var sorted = new SortedDictionary<string, string>(fields);
        var json = JsonSerializer.Serialize(sorted);
        var hash = ComputeHash(json);
        return new DraftContentResult(json, hash, new Dictionary<string, string>(sorted));
    }

    public DraftContentResult BuildForUpdate(LetterTemplateDto template, Dictionary<string, string> fields)
    {
        var allowed = template.Fields
            .Where(x => x.ValueSource is not ("server" or "signatureEvidence"))
            .Select(x => x.Key)
            .ToHashSet();

        var sanitized = fields
            .Where(x => allowed.Contains(x.Key) && x.Value != null && x.Value.Length <= 4000)
            .ToDictionary(x => x.Key, x => x.Value);

        var sorted = new SortedDictionary<string, string>(sanitized);
        var json = JsonSerializer.Serialize(sorted);
        var hash = ComputeHash(json);
        return new DraftContentResult(json, hash, sanitized);
    }

    public DraftContentResult BuildForEdit(LetterTemplateDto template, Dictionary<string, string>? fields)
    {
        var allowed = template.Fields
            .Where(x => x.ValueSource == "user")
            .Select(x => x.Key)
            .ToHashSet();

        if (fields == null || fields.Any(x => !allowed.Contains(x.Key) || x.Value == null || x.Value.Length > 4000
            || x.Value.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t')))
        {
            throw new SignItDomainException(DomainErrorKind.Validation, "invalid_draft_fields", "Field tidak sesuai schema.");
        }

        var sorted = new SortedDictionary<string, string>(fields);
        var json = JsonSerializer.Serialize(sorted);
        var hash = ComputeHash(json);
        return new DraftContentResult(json, hash, new Dictionary<string, string>(sorted));
    }

    private static string ComputeHash(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
