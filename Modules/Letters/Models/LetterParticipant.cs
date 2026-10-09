namespace SignIt.Modules.Letters.Models;

public sealed class LetterParticipant
{
    private LetterParticipant() { }

    public Guid Id { get; private set; }
    public Guid RevisionId { get; private set; }
    public LetterRole Role { get; private set; }
    public string SlotKey { get; private set; } = string.Empty;
    public Guid UserId { get; private set; }
    public string DisplayNameSnapshot { get; private set; } = string.Empty;
    public string? PositionSnapshot { get; private set; }
    public bool Required { get; private set; }
    public int PageIndex { get; private set; }
    public double X { get; private set; }
    public double Y { get; private set; }
    public double Width { get; private set; }
    public double Height { get; private set; }
    public double Rotation { get; private set; }

    public static LetterParticipant Create(
        Guid id,
        Guid revisionId,
        LetterRole role,
        string slotKey,
        Guid userId,
        string displayNameSnapshot,
        string? positionSnapshot,
        bool required,
        int pageIndex,
        double x,
        double y,
        double width,
        double height,
        double rotation = 0)
    {
        if (id == Guid.Empty) throw new ArgumentException("Id tidak boleh kosong.", nameof(id));
        if (revisionId == Guid.Empty) throw new ArgumentException("RevisionId tidak boleh kosong.", nameof(revisionId));
        if (userId == Guid.Empty) throw new ArgumentException("UserId tidak boleh kosong.", nameof(userId));
        if (string.IsNullOrWhiteSpace(slotKey)) throw new ArgumentException("SlotKey tidak boleh kosong.", nameof(slotKey));
        if (string.IsNullOrWhiteSpace(displayNameSnapshot)) throw new ArgumentException("DisplayNameSnapshot tidak boleh kosong.", nameof(displayNameSnapshot));
        if (pageIndex < 0) throw new ArgumentException("PageIndex harus >= 0.", nameof(pageIndex));
        if (width <= 0 || height <= 0) throw new ArgumentException("Lebar dan tinggi slot harus > 0.", nameof(width));

        return new LetterParticipant
        {
            Id = id,
            RevisionId = revisionId,
            Role = role,
            SlotKey = slotKey.Trim(),
            UserId = userId,
            DisplayNameSnapshot = displayNameSnapshot.Trim(),
            PositionSnapshot = positionSnapshot?.Trim(),
            Required = required,
            PageIndex = pageIndex,
            X = x,
            Y = y,
            Width = width,
            Height = height,
            Rotation = rotation
        };
    }
}
