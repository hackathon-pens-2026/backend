namespace SignIt.Modules.Authentication.Models;

public sealed class UserAssignment
{
    private UserAssignment() { }
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string PositionCode { get; private set; } = string.Empty;
    public string PositionName { get; private set; } = string.Empty;
    public string Scope { get; private set; } = string.Empty;
    public UserCapability Capability { get; private set; }
    public DateTimeOffset ValidFrom { get; private set; }
    public DateTimeOffset? ValidTo { get; private set; }
    public bool IsActive { get; private set; }

    public static UserAssignment Provision(Guid id, Guid userId, string positionCode,
        string positionName, string scope, UserCapability capability,
        DateTimeOffset validFrom, DateTimeOffset? validTo)
    {
        if (id == Guid.Empty || userId == Guid.Empty || string.IsNullOrWhiteSpace(positionCode)
            || string.IsNullOrWhiteSpace(positionName) || string.IsNullOrWhiteSpace(scope)
            || !Enum.IsDefined(capability) || validTo <= validFrom)
            throw new ArgumentException("Assignment tidak valid.");
        return new UserAssignment
        {
            Id = id, UserId = userId, PositionCode = positionCode, PositionName = positionName,
            Scope = scope, Capability = capability, ValidFrom = validFrom.ToUniversalTime(),
            ValidTo = validTo?.ToUniversalTime(), IsActive = true
        };
    }
}
