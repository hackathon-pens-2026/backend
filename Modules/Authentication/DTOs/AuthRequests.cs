using System.ComponentModel.DataAnnotations;
using SignIt.Modules.Authentication.Services;

namespace SignIt.Modules.Authentication.DTOs;

public sealed record LoginRequest(
    [Required, EmailAddress, StringLength(254)] string Email,
    [Required, StringLength(PasswordPolicy.MaximumLength)] string Password);

public sealed record RefreshRequest(
    [Required, StringLength(43, MinimumLength = 43)] string RefreshToken);

public sealed record ForgotPasswordRequest(
    [Required, EmailAddress, StringLength(254)] string Email);

public sealed record ResetPasswordRequest(
    [Required, StringLength(43, MinimumLength = 43)] string Token,
    [Required, StringLength(PasswordPolicy.MaximumLength)] string NewPassword);

public sealed record MessageResponse(string Message);
