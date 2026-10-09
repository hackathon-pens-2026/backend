using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SignIt.Infrastructure;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Persistence;
using SignIt.Infrastructure.Storage;
using SignIt.Modules.Authentication.Data;
using SignIt.Modules.Letters.Models;
using SignIt.Modules.Letters.Services;
using SignIt.Modules.Templates.Services;
using Xunit;

namespace SignIt.Signatures.Tests;

public sealed class PostgresPreviewTheoryAttribute : TheoryAttribute
{
    public PostgresPreviewTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SIGNIT_TEST_CONNECTION")))
            Skip = "Set SIGNIT_TEST_CONNECTION ke database PostgreSQL terpisah dengan nama berakhiran _tests.";
    }
}

public sealed class PreviewPostgresFixture : IAsyncLifetime
{
    public ServiceProvider Provider { get; private set; } = null!;
    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("SIGNIT_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return;
        if (!(new NpgsqlConnectionStringBuilder(connection).Database?.EndsWith("_tests", StringComparison.Ordinal) ?? false))
            throw new InvalidOperationException("Integration test tidak boleh menggunakan database aplikasi.");
        var environment = new TemplateTestEnvironment();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = connection,
            ["Storage:RootDirectory"] = ".data/renderer-integration/storage",
            ["Preview:Enabled"] = "false"
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddLogging(builder => builder.ClearProviders());
        services.AddSignItInfrastructure(configuration, environment);
        Provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
        foreach (var file in new[] { "organizations.sql", "facilities.sql" })
            await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(Path.Combine(environment.ContentRootPath, "provisioning", file)));
        await DemoAccountProvisioner.ProvisionAsync(db, scope.ServiceProvider.GetRequiredService<AuthProvisioner>(),
            Path.Combine(environment.ContentRootPath, ".data", "renderer-integration"), default);
    }
    public Task DisposeAsync() { Provider?.Dispose(); return Task.CompletedTask; }
}

[CollectionDefinition("Preview PostgreSQL", DisableParallelization = true)]
public sealed class PreviewPostgresCollection : ICollectionFixture<PreviewPostgresFixture> { }

[Collection("Preview PostgreSQL")]
public sealed class LetterPreviewPostgresTests(PreviewPostgresFixture fixture)
{
    [PostgresPreviewTheory]
    [Trait("Category", "Postgres")]
    [InlineData("proposal")]
    public async Task ConcurrentGenerateAndRenderFailure_KeepOneJobAndAllowRetry(string type)
    {
        using var scope = fixture.Provider.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AppDbContext>();
        var requester = await db.Users.SingleAsync(x => x.Email == "pengaju-ukki@demo.signit.example");
        var organization = await db.Organizations.SingleAsync(x => x.Scope == "ukki");
        var committee = await db.Users.SingleAsync(x => x.Email == "ketupel@demo.signit.example");
        var chair = await db.Users.SingleAsync(x => x.Email == "ketua@demo.signit.example");
        var template = (await services.GetRequiredService<TemplateCatalog>().GetAllAsync(default)).Single(x => x.TypeId == type);
        var values = TemplateRendererTests.Fields(template);
        var fields = template.Fields.Where(x => x.ValueSource == "user").ToDictionary(x => x.Key, x => values[x.Key]);
        var letters = services.GetRequiredService<LettersService>();
        var incomplete = await letters.CreateAsync(requester.Id, new(type, "QA Incomplete", []), default);
        var previewService = services.GetRequiredService<LetterPreviewService>();
        var incompleteRequest = new PreviewLetterRequest(incomplete.Version, incomplete.RevisionId, incomplete.ContentHash,
            organization.Id, committee.Id, chair.Id, null);
        var invalid = await Assert.ThrowsAsync<SignItDomainException>(() => previewService.QueueAsync(requester.Id, incomplete.Id, incompleteRequest, default));
        Assert.Equal("required_fields_missing", invalid.Code);
        Assert.False(await db.LetterPreviewJobs.AnyAsync(x => x.RequestId == incomplete.Id));
        var draft = await letters.CreateAsync(requester.Id, new(type, "QA Retry", fields), default);
        var request = new PreviewLetterRequest(draft.Version, draft.RevisionId, draft.ContentHash,
            organization.Id, committee.Id, chair.Id, null);
        async Task<LetterPreviewDto> Generate()
        {
            using var concurrentScope = fixture.Provider.CreateScope();
            return await concurrentScope.ServiceProvider.GetRequiredService<LetterPreviewService>().QueueAsync(requester.Id, draft.Id, request, default);
        }
        var generated = await Task.WhenAll(Generate(), Generate());
        Assert.Equal(generated[0].JobId, generated[1].JobId);
        var failedProcessor = new LetterPreviewProcessor(db, new FailingRenderer(), services.GetRequiredService<IStorageService>(),
            TimeProvider.System, new PreviewWorkerOptions(), NullLogger<LetterPreviewProcessor>.Instance);
        Assert.True(await failedProcessor.ProcessNextAsync(default));
        var failed = await previewService.GetAsync(requester.Id, draft.Id, generated[0].JobId, default);
        Assert.Equal("Failed", failed.State);
        Assert.Null(failed.ReviewDocumentId);
        Assert.Equal(LetterStatus.Draft, (await db.LetterRequests.SingleAsync(x => x.Id == draft.Id)).Status);
        var retry = await previewService.QueueAsync(requester.Id, draft.Id, request, default);
        Assert.Equal(failed.JobId, retry.JobId);
        Assert.True(await services.GetRequiredService<LetterPreviewProcessor>().ProcessNextAsync(default));
        var ready = await previewService.GetAsync(requester.Id, draft.Id, retry.JobId, default);
        Assert.Equal("Ready", ready.State);
        Assert.Equal(1, await db.Documents.CountAsync(x => x.RevisionId == draft.RevisionId));
    }

    private sealed class FailingRenderer : ILetterTemplateRenderer
    {
        public RenderedPreview Render(PreviewRenderInput input, CancellationToken ct)
            => throw new InvalidOperationException("Simulasi adapter renderer gagal.");
    }

    [PostgresPreviewTheory]
    [Trait("Category", "Postgres")]
    [InlineData("proposal", "himpunan-informatika-sains-data", null, 5)]
    [InlineData("lpj", "ukki", null, 5)]
    [InlineData("peminjaman-barang", "ukki", null, 6)]
    [InlineData("peminjaman-ruangan", "himpunan-informatika-sains-data", "PS", 6)]
    [InlineData("peminjaman-ruangan", "ukki", "SAW", 6)]
    [InlineData("peminjaman-ruangan", "ukki", "LAPANGAN_MERAH", 7)]
    public async Task DraftPreviewAndSubmission_UseFrozenServerLayoutAndPreserveAuthorization(
        string type, string organizationScope, string? facilityCode, int stageCount)
    {
        using var scope = fixture.Provider.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AppDbContext>();
        var requester = await db.Users.SingleAsync(x => x.Email == $"pengaju-{organizationScope}@demo.signit.example");
        var otherUser = await db.Users.FirstAsync(x => x.Email == "dagri@demo.signit.example");
        var organization = await db.Organizations.SingleAsync(x => x.Scope == organizationScope);
        var committee = await db.Users.SingleAsync(x => x.Email == "ketupel@demo.signit.example");
        var chair = await db.Users.SingleAsync(x => x.Email == "ketua@demo.signit.example");
        Guid? resourceId = null;
        if (facilityCode != null)
        {
            var facility = await db.Facilities.SingleAsync(x => x.Code == facilityCode);
            resourceId = await db.FacilityResources.Where(x => x.FacilityId == facility.Id && x.IsBookable).Select(x => x.Id).FirstAsync();
        }
        var template = (await services.GetRequiredService<TemplateCatalog>().GetAllAsync(default)).Single(x => x.TypeId == type);
        var values = TemplateRendererTests.Fields(template);
        var userFields = template.Fields.Where(x => x.ValueSource == "user").ToDictionary(x => x.Key, x => values[x.Key]);
        var letters = services.GetRequiredService<LettersService>();
        var draft = await letters.CreateAsync(requester.Id, new(type, "QA Renderer " + type, userFields), default);
        var previews = services.GetRequiredService<LetterPreviewService>();
        var request = new PreviewLetterRequest(draft.Version, draft.RevisionId, draft.ContentHash,
            organization.Id, committee.Id, chair.Id, resourceId);
        var deniedQueue = await Assert.ThrowsAsync<SignItDomainException>(() => previews.QueueAsync(otherUser.Id, draft.Id, request, default));
        Assert.Equal(DomainErrorKind.NotFound, deniedQueue.Kind);
        var staleDraft = await Assert.ThrowsAsync<SignItDomainException>(() => previews.QueueAsync(requester.Id, draft.Id,
            request with { ExpectedVersion = Guid.NewGuid() }, default));
        Assert.Equal("stale_draft", staleDraft.Code);
        var queued = await previews.QueueAsync(requester.Id, draft.Id, request, default);
        var repeated = await previews.QueueAsync(requester.Id, draft.Id, request, default);
        Assert.Equal(queued.JobId, repeated.JobId);
        var denied = await Assert.ThrowsAsync<SignItDomainException>(() => previews.GetAsync(otherUser.Id, draft.Id, queued.JobId, default));
        Assert.Equal(DomainErrorKind.NotFound, denied.Kind);
        var pending = await previews.GetAsync(requester.Id, draft.Id, queued.JobId, default);
        for (var attempt = 0; attempt < 30 && pending.State == "Pending"; attempt++)
        {
            Assert.True(await services.GetRequiredService<LetterPreviewProcessor>().ProcessNextAsync(default));
            pending = await previews.GetAsync(requester.Id, draft.Id, queued.JobId, default);
        }
        Assert.Equal("Ready", pending.State);
        Assert.Equal(stageCount, pending.Slots.Length);
        Assert.Equal(LetterStatus.Draft, (await db.LetterRequests.SingleAsync(x => x.Id == draft.Id)).Status);
        Assert.False(await db.SignatureEvidences.AnyAsync(x => x.RevisionId == draft.RevisionId));
        var bytes = await previews.DownloadAsync(requester.Id, draft.Id, pending.ReviewDocumentId!.Value, default);
        Assert.Equal(pending.ReviewHash, LetterPreviewService.Hash(bytes));
        await Assert.ThrowsAsync<SignItDomainException>(() => previews.DownloadAsync(otherUser.Id, draft.Id, pending.ReviewDocumentId.Value, default));
        var submit = new SubmitLetterRequest(draft.Version, draft.RevisionId, draft.ContentHash,
            organization.Id, committee.Id, chair.Id, resourceId, pending.ReviewDocumentId.Value, pending.ReviewHash!, pending.Slots);
        var submissions = services.GetRequiredService<LetterSubmissionService>();
        var changedSlots = pending.Slots.ToArray();
        changedSlots[0] = changedSlots[0] with { X = changedSlots[0].X + 1 };
        var slotError = await Assert.ThrowsAsync<SignItDomainException>(() => submissions.SubmitAsync(requester.Id, draft.Id,
            submit with { Slots = changedSlots }, "changed-slots", default));
        Assert.Equal("preview_slots_mismatch", slotError.Code);
        db.ChangeTracker.Clear();
        if (resourceId is not null)
        {
            var another = await db.FacilityResources.Where(x => x.IsBookable && x.Id != resourceId).Select(x => x.Id).FirstAsync();
            var stale = await Assert.ThrowsAsync<SignItDomainException>(() => submissions.SubmitAsync(requester.Id, draft.Id,
                submit with { ResourceId = another }, "changed-resource", default));
            Assert.Equal("stale_preview", stale.Code);
            db.ChangeTracker.Clear();
        }
        var result = await submissions.SubmitAsync(requester.Id, draft.Id, submit, "submit-qa", default);
        var retry = await submissions.SubmitAsync(requester.Id, draft.Id, submit, "submit-qa", default);
        Assert.Equal(result.RevisionId, retry.RevisionId);
        Assert.Equal(stageCount, await db.WorkflowTasks.CountAsync(x => x.RevisionId == result.RevisionId));
        Assert.Equal(stageCount, await db.LetterParticipants.CountAsync(x => x.RevisionId == result.RevisionId));
        Assert.Equal(1, await db.Documents.CountAsync(x => x.RevisionId == draft.RevisionId && x.Kind == DocumentKind.Review));
    }
}
