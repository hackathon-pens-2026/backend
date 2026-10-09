namespace SignIt.Modules.Chat.Models;

public sealed class ChatSession
{
    private ChatSession() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid? LetterRequestId { get; private set; }
    public string? TypeId { get; private set; }
    public ChatSessionStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static ChatSession Create(
        Guid id,
        Guid userId,
        Guid? letterRequestId,
        string? typeId,
        DateTimeOffset now)
    {
        if (id == Guid.Empty) throw new ArgumentException("Id tidak boleh kosong.", nameof(id));
        if (userId == Guid.Empty) throw new ArgumentException("UserId tidak boleh kosong.", nameof(userId));

        return new ChatSession
        {
            Id = id,
            UserId = userId,
            LetterRequestId = letterRequestId,
            TypeId = typeId?.Trim(),
            Status = ChatSessionStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public void LinkLetterRequest(Guid letterRequestId, DateTimeOffset now)
    {
        if (letterRequestId == Guid.Empty) throw new ArgumentException("LetterRequestId tidak boleh kosong.", nameof(letterRequestId));
        LetterRequestId = letterRequestId;
        UpdatedAt = now;
    }

    public void SetTypeId(string typeId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(typeId)) throw new ArgumentException("TypeId tidak boleh kosong.", nameof(typeId));
        TypeId = typeId.Trim();
        UpdatedAt = now;
    }

    public void Complete(DateTimeOffset now)
    {
        Status = ChatSessionStatus.Completed;
        UpdatedAt = now;
    }

    public void Abandon(DateTimeOffset now)
    {
        Status = ChatSessionStatus.Abandoned;
        UpdatedAt = now;
    }

    public void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
    }
}
