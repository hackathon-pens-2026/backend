using System.Text.RegularExpressions;

namespace SignIt.Infrastructure.Llm;

public static class LlmSanitizer
{
    private static readonly Regex JwtRegex = new(
        @"eyJ[a-zA-Z0-9-_=]+\.eyJ[a-zA-Z0-9-_=]+\.[a-zA-Z0-9-_=]+",
        RegexOptions.Compiled);

    private static readonly Regex ApiKeyRegex = new(
        @"(?:sk|key|token|secret)_[a-zA-Z0-9]{20,}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PasswordRegex = new(
        @"(?:password|kata\s*sandi|pwd)\s*[:=]\s*[^\s,;]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BearerRegex = new(
        @"Bearer\s+[^\s,;]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Sanitize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var text = input;
        text = JwtRegex.Replace(text, "[REDACTED_JWT]");
        text = BearerRegex.Replace(text, "Bearer [REDACTED_TOKEN]");
        text = ApiKeyRegex.Replace(text, "[REDACTED_API_KEY]");
        text = PasswordRegex.Replace(text, "password: [REDACTED_PASSWORD]");

        return text;
    }
}
