using SignIt.Modules.Templates.Services;

namespace SignIt.Modules.Letters.Services;

public sealed record DraftContentResult(string Json, string ContentHash, Dictionary<string, string> SanitizedFields);

public interface IDraftContentBuilder
{
    DraftContentResult BuildForCreation(LetterTemplateDto template, Dictionary<string, string>? fields);
    DraftContentResult BuildForUpdate(LetterTemplateDto template, Dictionary<string, string> fields);
    DraftContentResult BuildForEdit(LetterTemplateDto template, Dictionary<string, string>? fields);
}
