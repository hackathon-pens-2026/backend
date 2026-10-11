using Microsoft.EntityFrameworkCore;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Persistence;
using SignIt.Modules.Letters.Models;
using SignIt.Modules.Workflow.Models;
using SignIt.Modules.Workflow.Services;

namespace SignIt.Modules.Letters.Services;

public sealed class LetterQueryService : ILetterQueryService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public LetterQueryService(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<LetterListDto> ListAsync(Guid actor, int page, int pageSize, string? search, CancellationToken ct)
    {
        if (page is < 1 or > 10000 || pageSize is < 1 or > 100 || search?.Length > 300)
        {
            throw new SignItDomainException(DomainErrorKind.Validation, "invalid_pagination", "Page mulai 1; pageSize maksimal 100.");
        }

        var query = _db.LetterRequests.AsNoTracking().Where(x => x.SubmittedByUserId == actor);
        var counts = await query.GroupBy(x => x.Status).Select(x => new { Status = x.Key, Count = x.Count() }).ToArrayAsync(ct);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.Title.Contains(term) || x.Number.Contains(term));
        }

        var total = await query.CountAsync(ct);
        var letters = await query.OrderByDescending(x => x.SubmittedAt).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        var revisionIds = letters.Where(x => x.CurrentRevisionId.HasValue).Select(x => x.CurrentRevisionId!.Value).ToArray();
        var tasks = await _db.WorkflowTasks.AsNoTracking().Where(x => revisionIds.Contains(x.RevisionId)).ToListAsync(ct);

        var orgIds = letters.Where(x => x.OrganizationId.HasValue).Select(x => x.OrganizationId!.Value).Distinct().ToArray();
        var orgScopes = await _db.Organizations.AsNoTracking().Where(x => orgIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Scope, ct);

        var assignedIds = tasks.Select(x => x.AssignedUserId).Distinct().ToArray();
        var positionCodes = tasks.Where(x => x.DomainCode != null).Select(x => x.DomainCode!).Distinct().ToArray();
        var scopes = orgScopes.Values.Distinct().ToArray();

        var positions = await _db.Assignments.AsNoTracking()
            .Where(x => assignedIds.Contains(x.UserId) && x.PositionCode != null
                && positionCodes.Contains(x.PositionCode) && scopes.Contains(x.Scope))
            .Select(x => new { x.UserId, x.PositionCode, x.PositionName, x.Scope }).ToListAsync(ct);

        var now = _clock.GetUtcNow();
        var items = letters.Select(letter =>
        {
            var current = tasks.Where(x => x.RevisionId == letter.CurrentRevisionId).ToArray();
            var active = current.SingleOrDefault(x => x.Status == WorkflowTaskStatus.Active);
            var scope = letter.OrganizationId.HasValue && orgScopes.TryGetValue(letter.OrganizationId.Value, out var value) ? value : null;
            var positionName = active is null ? null : positions.FirstOrDefault(p => p.UserId == active.AssignedUserId
                && p.PositionCode == active.DomainCode && p.Scope == scope)?.PositionName;

            return new LetterSummaryDto(letter.Id, letter.Number, letter.TypeId, letter.Title, letter.Status,
                letter.RowVersion, letter.CurrentRevisionId ?? Guid.Empty, letter.SubmittedAt, letter.CompletedAt,
                current.Length, current.Count(x => WorkflowTaskAccess.IsDone(x.Status)),
                active is null ? null : new LetterActiveTaskDto(active.Id, active.Order, active.ActionType,
                    active.DomainCode, positionName, active.ActivatedAt, active.DueAt,
                    active.DueAt.HasValue && active.DueAt < now));
        }).ToArray();

        return new LetterListDto(page, pageSize, total, items, counts.ToDictionary(x => x.Status.ToString(), x => x.Count));
    }
}
