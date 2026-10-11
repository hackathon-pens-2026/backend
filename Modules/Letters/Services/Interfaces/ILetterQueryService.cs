namespace SignIt.Modules.Letters.Services;

public interface ILetterQueryService
{
    Task<LetterListDto> ListAsync(Guid actor, int page, int pageSize, string? search, CancellationToken ct);
}
