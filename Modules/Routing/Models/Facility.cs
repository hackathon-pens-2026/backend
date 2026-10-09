namespace SignIt.Modules.Routing.Models;

public sealed class Facility
{
    public Guid Id { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public bool RequiresDagri { get; private set; }
}

public sealed class FacilityResource
{
    public Guid Id { get; private set; }
    public Guid FacilityId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public int? Floor { get; private set; }
    public bool IsBookable { get; private set; }
}
