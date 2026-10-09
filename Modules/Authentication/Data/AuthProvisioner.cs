using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SignIt.Modules.Authentication.Services;
using SignIt.Modules.Authentication.Models;
using SignIt.Infrastructure.Persistence;

namespace SignIt.Modules.Authentication.Data;

// Invoked explicitly from the CLI, never from an HTTP endpoint or application startup.
public sealed class AuthProvisioner(AppDbContext db, IPasswordService passwords, AuthOptions options, TimeProvider clock)
{
    public async Task<int> ProvisionAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        jsonOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        jsonOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        var manifest = await JsonSerializer.DeserializeAsync<ProvisionManifest>(stream, jsonOptions, ct)
            ?? throw new InvalidOperationException("Manifest provisioning kosong.");
        if (manifest.Users is null || manifest.Users.Count == 0 || manifest.Users.Count > 1000 || manifest.Users.Any(x => x is null))
            throw new InvalidOperationException("Manifest harus berisi 1–1000 akun.");
        foreach (var account in manifest.Users) ValidateAccount(account);
        if (manifest.Users.Select(x => x.Id).Distinct().Count() != manifest.Users.Count
            || manifest.Users.Select(x => User.NormalizeEmail(x.Email)).Distinct().Count() != manifest.Users.Count)
            throw new InvalidOperationException("Manifest memiliki ID/email duplikat.");

        var created = 0;
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Provisioning is an operational command; serialize concurrent executions of the same manifest.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(1936287598)", ct);
        foreach (var account in manifest.Users)
        {
            var normalized = User.NormalizeEmail(account.Email);
            var existing = await db.Users.SingleOrDefaultAsync(x => x.Id == account.Id || x.NormalizedEmail == normalized, ct);
            if (existing is not null)
            {
                if (existing.Id != account.Id || existing.NormalizedEmail != normalized || existing.Category != account.Category
                    || existing.Name != account.Name.Trim() || existing.NimNip != account.NimNip)
                    throw new InvalidOperationException("Akun seed berbeda dari data tersimpan. Gunakan script koreksi ter-audit.");
                // Do not read the initial secret or reset an existing password.
            }
            else
            {
                if (string.IsNullOrWhiteSpace(account.PasswordEnvironmentVariable))
                    throw new InvalidOperationException("Nama environment variable password wajib diisi untuk akun baru.");
                var password = Environment.GetEnvironmentVariable(account.PasswordEnvironmentVariable)
                    ?? throw new InvalidOperationException($"Secret provisioning {account.PasswordEnvironmentVariable} belum tersedia.");
                PasswordPolicy.Validate(password, options);
                db.Users.Add(User.Provision(account.Id, account.Name, account.Email, account.NimNip,
                    account.Category, passwords.Hash(password), now, account.EmailVerified));
                db.Audits.Add(AuthAudit.Record(account.Id, "auth.account_provisioned", now, null, null, "provision-auth"));
                created++;
            }

            foreach (var assignment in account.Assignments)
            {
                if (assignment is null || string.IsNullOrWhiteSpace(assignment.PositionCode)
                    || string.IsNullOrWhiteSpace(assignment.PositionName) || string.IsNullOrWhiteSpace(assignment.Scope)
                    || assignment.PositionCode.Length > 80 || assignment.PositionName.Length > 150 || assignment.Scope.Length > 200)
                    throw new InvalidOperationException("Field assignment kosong atau melebihi batas panjang.");
                var candidate = UserAssignment.Provision(assignment.Id, account.Id, assignment.PositionCode,
                    assignment.PositionName, assignment.Scope, assignment.Capability, assignment.ValidFrom, assignment.ValidTo);
                var saved = await db.Assignments.SingleOrDefaultAsync(x => x.Id == assignment.Id, ct);
                if (saved is not null)
                {
                    if (saved.UserId != account.Id || saved.PositionCode != candidate.PositionCode
                        || saved.PositionName != candidate.PositionName || saved.Scope != candidate.Scope
                        || saved.Capability != candidate.Capability || saved.ValidFrom != candidate.ValidFrom || saved.ValidTo != candidate.ValidTo)
                        throw new InvalidOperationException("Assignment seed berbeda dari data tersimpan. Jangan menulis ulang riwayat.");
                    continue;
                }
                db.Assignments.Add(candidate);
                db.Audits.Add(AuthAudit.Record(account.Id, "auth.assignment_provisioned", now, null, null, "provision-auth"));
            }
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return created;
    }

    private static void ValidateAccount(ProvisionAccount account)
    {
        if (account.Id == Guid.Empty || string.IsNullOrWhiteSpace(account.Name) || account.Name.Length > 150
            || string.IsNullOrWhiteSpace(account.Email) || account.Email.Length > 254 || !MailAddress.TryCreate(account.Email.Trim(), out var email)
            || email.Address != account.Email.Trim() || account.NimNip?.Length > 50 || !Enum.IsDefined(account.Category)
            || account.Assignments is null || account.Assignments.Count > 100)
            throw new InvalidOperationException("Data akun dalam manifest tidak valid.");
    }

    private sealed record ProvisionManifest([property: JsonRequired] List<ProvisionAccount> Users);
    private sealed record ProvisionAccount([property: JsonRequired] Guid Id,
        [property: JsonRequired] string Name, [property: JsonRequired] string Email, string? NimNip,
        [property: JsonRequired] UserCategory Category, string PasswordEnvironmentVariable, bool EmailVerified,
        [property: JsonRequired] List<ProvisionAssignment> Assignments);
    private sealed record ProvisionAssignment([property: JsonRequired] Guid Id,
        [property: JsonRequired] string PositionCode, [property: JsonRequired] string PositionName,
        [property: JsonRequired] string Scope, [property: JsonRequired] UserCapability Capability,
        [property: JsonRequired] DateTimeOffset ValidFrom, DateTimeOffset? ValidTo);
}
