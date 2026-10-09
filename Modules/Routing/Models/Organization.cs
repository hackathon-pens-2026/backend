namespace SignIt.Modules.Routing.Models;

public sealed class Organization
{
    public Guid Id { get; private set; }
    public string Scope { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string Kind { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
}
