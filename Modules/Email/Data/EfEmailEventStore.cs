using Microsoft.EntityFrameworkCore;
using Npgsql;
using SignIt.Infrastructure.Persistence;
using SignIt.Modules.Email.Models;
using SignIt.Modules.Email.Services;

namespace SignIt.Modules.Email.Data;

public sealed class EfEmailEventStore(AppDbContext db) : IEmailEventStore
{
    public async Task<bool> TryRecordEventAsync(EmailProviderEvent providerEvent,
        IReadOnlyList<EmailSuppression> suppressions, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.EmailProviderEvents.Add(providerEvent);
        foreach (var suppression in suppressions)
        {
            var address = suppression.Email;
            var existing = await db.EmailSuppressions.SingleOrDefaultAsync(x => x.Email == address, ct);
            if (existing is null) db.EmailSuppressions.Add(suppression);
            else existing.Refresh(suppression.Reason, suppression.Source, suppression.SuppressedAt);
        }

        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A replayed provider event: keep the first outcome and acknowledge the duplicate.
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
