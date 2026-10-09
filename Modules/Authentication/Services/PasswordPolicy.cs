using SignIt.Modules.Authentication.DTOs;

namespace SignIt.Modules.Authentication.Services;

public static class PasswordPolicy
{
    public const int MaximumLength = 128;

    public static void Validate(string password, AuthOptions options)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < options.MinimumPasswordLength
            || password.Length > MaximumLength)
            throw new AuthException("password_policy_failed",
                $"Password harus berisi {options.MinimumPasswordLength}–{MaximumLength} karakter.", AuthErrorKind.Validation);
    }
}
