using System.ComponentModel.DataAnnotations;
using SignIt.Modules.Authentication.Services;

namespace SignIt.Modules.Authentication.DTOs;

public sealed record LoginRequest(
    [property: Required, EmailAddress, StringLength(254)] string Email,
    [property: Required, StringLength(PasswordPolicy.MaximumLength)] string Password);

public sealed record RefreshRequest(
    [property: Required, StringLength(43, MinimumLength = 43)] string RefreshToken);

public sealed record ForgotPasswordRequest(
    [property: Required, EmailAddress, StringLength(254)] string Email);

public sealed record ResetPasswordRequest(
    [property: Required, StringLength(43, MinimumLength = 43)] string Token,
    [property: Required, StringLength(PasswordPolicy.MaximumLength)] string NewPassword);

public sealed record MessageResponse(string Message);
