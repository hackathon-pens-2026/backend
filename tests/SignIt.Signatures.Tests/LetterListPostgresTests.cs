using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Persistence;
using SignIt.Modules.Letters.Services;
using Xunit;

namespace SignIt.Signatures.Tests;

[Collection("Preview PostgreSQL")]
public sealed class LetterListPostgresTests(PreviewPostgresFixture fixture)
{
    [PostgresPreviewTheory]
    [InlineData("proposal")]
    public async Task List_IsOwnerScopedPaginatedAndSearchable(string type)
    {
        using var scope = fixture.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<LettersService>();
        var owner = await db.Users.SingleAsync(x => x.Email == "pengaju-ukki@demo.signit.example");
        var other = await db.Users.SingleAsync(x => x.Email == "pengaju-bem@demo.signit.example");
        var prefix = "List-QA-" + Guid.NewGuid().ToString("N");
        var first = await service.CreateAsync(owner.Id, new(type, prefix + "-one", []), default);
        var second = await service.CreateAsync(owner.Id, new(type, prefix + "-two", []), default);
        var hidden = await service.CreateAsync(other.Id, new(type, prefix + "-private", []), default);
        var page1 = await service.ListAsync(owner.Id, 1, 1, prefix, default);
        var page2 = await service.ListAsync(owner.Id, 2, 1, prefix, default);
        Assert.Equal(2, page1.Total);
        Assert.Single(page1.Items);
        Assert.Single(page2.Items);
        Assert.NotEqual(page1.Items[0].Id, page2.Items[0].Id);
        Assert.Equal(new[] { first.Id, second.Id }.Order(), page1.Items.Concat(page2.Items).Select(x => x.Id).Order());
        Assert.DoesNotContain(page1.Items.Concat(page2.Items), x => x.Id == hidden.Id);
        Assert.Equal(await db.LetterRequests.CountAsync(x => x.SubmittedByUserId == owner.Id), page1.StatusCounts.Values.Sum());
        var empty = await service.ListAsync(owner.Id, 1, 20, prefix + "-private", default);
        Assert.Equal(0, empty.Total);
        Assert.Empty(empty.Items);
        var invalid = await Assert.ThrowsAsync<SignItDomainException>(() => service.ListAsync(owner.Id, 0, 20, null, default));
        Assert.Equal("invalid_pagination", invalid.Code);
    }
}
