using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Persistence;
using SignIt.Modules.Authentication.Models;
using SignIt.Modules.Routing.Services;
using Xunit;

namespace SignIt.Signatures.Tests;

[Collection("Preview PostgreSQL")]
public sealed class RoutingPostgresTests(PreviewPostgresFixture fixture)
{
    [PostgresPreviewTheory]
    [InlineData("proposal", null, 5)]
    [InlineData("lpj", null, 5)]
    [InlineData("peminjaman-barang", null, 6)]
    [InlineData("peminjaman-ruangan", "PS", 6)]
    [InlineData("peminjaman-ruangan", "SAW", 6)]
    [InlineData("peminjaman-ruangan", "D3", 7)]
    [InlineData("peminjaman-ruangan", "D4", 7)]
    [InlineData("peminjaman-ruangan", "LAPANGAN_MERAH", 7)]
    [InlineData("peminjaman-ruangan", "LAPANGAN_FUTSAL", 7)]
    [InlineData("peminjaman-ruangan", "LAPANGAN_BASKET", 7)]
    public async Task CampusChains_ResolveForBothOrganizationKinds(string type, string? facilityCode, int count)
    {
        using var scope = fixture.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var resolver = scope.ServiceProvider.GetRequiredService<RoutingService>();
        var committee = await db.Users.SingleAsync(x => x.Email == "ketupel@demo.signit.example");
        var chair = await db.Users.SingleAsync(x => x.Email == "ketua@demo.signit.example");
        Guid? resource = facilityCode == null ? null : await db.FacilityResources.Where(x => x.IsBookable
            && db.Facilities.Any(f => f.Id == x.FacilityId && f.Code == facilityCode)).Select(x => x.Id).FirstAsync();
        foreach (var kind in new[] { "Himpunan", "Organisasi" })
        {
            var organization = await db.Organizations.Where(x => x.Kind == kind).OrderBy(x => x.Scope).FirstAsync();
            var owner = await db.Users.SingleAsync(x => x.Email == "pengaju-" + organization.Scope + "@demo.signit.example");
            var stages = await resolver.ResolveAsync(owner.Id, new(type, organization.Id, committee.Id, chair.Id, resource), default);
            Assert.Equal(count, stages.Count);
            var expected = new List<string> { "Ketupel", "KetuaOrganisasi", "Pembina", kind == "Himpunan" ? "Kemahasiswaan" : "MinatBakat" };
            if (type == "peminjaman-ruangan")
            {
                if (count == 7) expected.Add("Dagri");
                expected.Add("BAAK");
            }
            expected.Add("Wadir3");
            if (type == "peminjaman-barang") expected.Add("Wadir2");
            Assert.Equal(expected, stages.Select(x => x.PositionCode));
            Assert.Equal(Enumerable.Range(1, count), stages.Select(x => x.Order));
            Assert.Equal(committee.Id, stages[0].UserId);
            Assert.Equal(chair.Id, stages[1].UserId);
        }
    }

    [PostgresPreviewTheory]
    [InlineData("pembina", null)]
    [InlineData("minat-bakat", null)]
    [InlineData("kemahasiswaan", null)]
    [InlineData("wadir3", null)]
    [InlineData("dagri", "D3")]
    [InlineData("baak", "PS")]
    [InlineData("wadir2", "item")]
    public async Task EveryApprover_IsBlockedFromApprovingOwnSubmission(string role, string? facilityCode)
    {
        using var scope = fixture.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var organization = role == "kemahasiswaan"
            ? await db.Organizations.Where(x => x.Kind == "Himpunan").OrderBy(x => x.Scope).FirstAsync()
            : await db.Organizations.SingleAsync(x => x.Scope == "ukki");
        var owner = await db.Users.SingleAsync(x => x.Email == role + "@demo.signit.example");
        var committee = await db.Users.SingleAsync(x => x.Email == "ketupel@demo.signit.example");
        var chair = await db.Users.SingleAsync(x => x.Email == "ketua@demo.signit.example");
        db.Assignments.Add(UserAssignment.Provision(Guid.NewGuid(), owner.Id, "Pengaju", "Pengaju Uji",
            organization.Scope, UserCapability.Requester, DateTimeOffset.UtcNow.AddDays(-1), null));
        await db.SaveChangesAsync();
        Guid? resource = facilityCode is null or "item" ? null : await db.FacilityResources.Where(x => x.IsBookable
            && db.Facilities.Any(f => f.Id == x.FacilityId && f.Code == facilityCode)).Select(x => x.Id).FirstAsync();
        var type = facilityCode == "item" ? "peminjaman-barang" : facilityCode == null ? "proposal" : "peminjaman-ruangan";
        var error = await Assert.ThrowsAsync<SignItDomainException>(() => scope.ServiceProvider.GetRequiredService<RoutingService>()
            .ResolveAsync(owner.Id, new(type, organization.Id, committee.Id, chair.Id, resource), default));
        Assert.Equal("self_approval_blocked", error.Code);
        await transaction.RollbackAsync();
    }

    [PostgresPreviewTheory]
    [InlineData(true, "requester_scope_denied")]
    [InlineData(false, "routing_unresolved")]
    public async Task CrossScopeRequesterAndUnassignedParticipant_AreRejected(bool crossScope, string expected)
    {
        using var scope = fixture.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var organization = await db.Organizations.SingleAsync(x => x.Scope == "ukki");
        var owner = await db.Users.SingleAsync(x => x.Email == (crossScope ? "pengaju-bem" : "pengaju-ukki") + "@demo.signit.example");
        var committee = await db.Users.SingleAsync(x => x.Email == (crossScope ? "ketupel" : "pengaju-bem") + "@demo.signit.example");
        var chair = await db.Users.SingleAsync(x => x.Email == "ketua@demo.signit.example");
        var error = await Assert.ThrowsAsync<SignItDomainException>(() => scope.ServiceProvider.GetRequiredService<RoutingService>()
            .ResolveAsync(owner.Id, new("proposal", organization.Id, committee.Id, chair.Id), default));
        Assert.Equal(expected, error.Code);
    }

    [PostgresPreviewTheory]
    [InlineData("uat-assignment-start.sql")]
    public async Task UatStartCorrection_IsScopedAuditedAndIdempotent(string file)
    {
        using var scope = fixture.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var user = User.Provision(Guid.Parse("aa202610-0010-4000-8000-000000000099"), "UAT Date QA",
            "uat.date-qa@demo.signit.example", null, UserCategory.StudentGeneral, "test-only-hash", DateTimeOffset.UtcNow, false);
        var oldStart = DateTimeOffset.Parse("2026-10-10T00:00:00Z");
        var correctedStart = DateTimeOffset.Parse("2026-10-09T17:00:00Z");
        var assignment = UserAssignment.Provision(Guid.Parse("ab202610-0010-4000-8000-000000000990"), user.Id,
            "Pengaju", "Pengaju UAT QA", "uat-himpunan", UserCapability.Requester, oldStart, null);
        var unrelated = UserAssignment.Provision(Guid.NewGuid(), user.Id, "Pengaju", "Pengaju Non-UAT QA",
            "ukki", UserCapability.Requester, oldStart, null);
        db.Users.Add(user);
        db.Assignments.AddRange(assignment, unrelated);
        await db.SaveChangesAsync();
        var sql = (await File.ReadAllTextAsync(Path.Combine(new TemplateTestEnvironment().ContentRootPath, "provisioning", file)))
            .Replace("BEGIN;", "").Replace("COMMIT;", "");
        await db.Database.ExecuteSqlRawAsync(sql);
        await db.Entry(assignment).ReloadAsync();
        await db.Entry(unrelated).ReloadAsync();
        Assert.Equal(correctedStart, assignment.ValidFrom);
        Assert.Equal(oldStart, unrelated.ValidFrom);
        await db.Database.ExecuteSqlRawAsync(sql);
        Assert.Equal(1, await db.Audits.CountAsync(x => x.UserId == user.Id && x.Action == "auth.uat_assignment_start_corrected"));
        await transaction.RollbackAsync();
    }
}
