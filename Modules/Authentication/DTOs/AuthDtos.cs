using SignIt.Modules.Authentication.Models;

namespace SignIt.Modules.Authentication.DTOs;

public sealed record AssignmentDto(Guid Id, string PositionCode, string PositionName,
    string Scope, UserCapability Capability, DateTimeOffset ValidFrom, DateTimeOffset? ValidTo);
public sealed record CapabilitiesDto(UserCategory UserCategory, UiSurface UiSurface,
    IReadOnlyList<UserCapability> Capabilities, IReadOnlyList<AssignmentDto> Assignments);
public sealed record UserDto(Guid Id, string Name, string Email, string? NimNip,
    bool IsActive, DateTimeOffset? EmailVerifiedAt, UserCategory UserCategory, UiSurface UiSurface,
    IReadOnlyList<UserCapability> Capabilities, IReadOnlyList<AssignmentDto> Assignments);
public sealed record AuthTokensDto(string AccessToken, DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken, DateTimeOffset RefreshTokenExpiresAt, UserDto User, string TokenType = "Bearer");
public sealed record RequestMetadata(string? IpAddress, string? UserAgent, string CorrelationId);
public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);

public enum AuthErrorKind { Validation, Unauthorized, Conflict }

public sealed class AuthException(string code, string message, AuthErrorKind kind) : Exception(message)
{
    public string Code { get; } = code;
    public AuthErrorKind Kind { get; } = kind;
}
