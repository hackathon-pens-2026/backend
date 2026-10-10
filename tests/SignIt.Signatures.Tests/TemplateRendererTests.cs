using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using PdfSharp.Pdf.IO;
using SignIt.Modules.Letters.Models;
using SignIt.Modules.Routing.Services;
using SignIt.Modules.Templates.Services;
using Xunit;

namespace SignIt.Signatures.Tests;

public sealed class TemplateTestEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "SignIt.Tests";
    public string ContentRootPath { get; set; } = FindBackend();
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    private static string FindBackend()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "backend.csproj"))) return directory.FullName;
        throw new InvalidOperationException("Root backend tidak ditemukan.");
    }
}

public sealed class TemplateRendererTests
{
    [Fact]
    public void PdfFonts_AreAvailableForRegularAndBoldSerifAndSans()
    {
        var resolver = new SignIt.Modules.Signatures.Services.SignItFontResolver();
        foreach (var family in new[] { "Times New Roman", "Arial" })
        foreach (var bold in new[] { false, true })
        {
            var face = resolver.ResolveTypeface(family, bold, false);
            Assert.NotNull(face);
            var bytes = resolver.GetFont(face.FaceName);
            Assert.NotNull(bytes);
            Assert.NotEmpty(bytes);
        }
    }

    internal static Dictionary<string, string> Fields(LetterTemplateDto template)
    {
        var values = template.Fields.ToDictionary(x => x.Key, x => x.ValueSource == "user" ? "Contoh " + x.Label.ToLowerInvariant() : "");
        values["nama_kegiatan"] = "Pelatihan Teknologi Mahasiswa";
        values["tahun_kegiatan"] = "2026";
        values["nama_ormawa"] = "Himpunan Mahasiswa Teknik Informatika dan Sains Data";
        values["nama_kampus"] = "Politeknik Elektronika Negeri Surabaya";
        values["nama_divisi_pelaksana"] = "Divisi Pengembangan Mahasiswa";
        values["kota_surat"] = "Surabaya";
        values["tanggal_surat"] = values["tanggal_pengesahan"] = "9 Oktober 2026";
        values["nomor_surat"] = "DRAFT - nomor resmi belum terbit";
        values["hari_tanggal_kegiatan"] = "Sabtu, 17 Oktober 2026";
        values["waktu_kegiatan"] = "08.00 - 16.00 WIB";
        values["ruangan_kegiatan"] = "Pasca Sarjana - PS 05.10";
        values["nama_pembina_ormawa"] = "Demo Pembina Organisasi";
        values["nama_penanggung_jawab"] = "Demo Ketua Organisasi";
        values["nama_ketua_pelaksana"] = "Demo Ketua Pelaksana";
        values["nama_pembina_minat_bakat"] = "Demo Kemahasiswaan";
        values["nama_barang_1"] = "Proyektor";
        values["jumlah_barang_1"] = "1 unit";
        values["pengambilan_barang_1"] = "17 Oktober 2026, 08.00";
        values["pengembalian_barang_1"] = "17 Oktober 2026, 16.00";
        values["nama_barang_2"] = "Sound System";
        values["jumlah_barang_2"] = "1 set";
        values["pengambilan_barang_2"] = "17 Oktober 2026, 08.00";
        values["pengembalian_barang_2"] = "17 Oktober 2026, 16.00";
        return values;
    }

    internal static RenderParticipant[] Participants(int count, string? type = null)
    {
        string[] codes = count == 5 ? ["Ketupel", "KetuaOrganisasi", "Pembina", "Kemahasiswaan", "Wadir3"]
            : type == "peminjaman-barang" ? ["Ketupel", "KetuaOrganisasi", "Pembina", "Kemahasiswaan", "Wadir3", "Wadir2"]
            : ["Ketupel", "KetuaOrganisasi", "Pembina", "Kemahasiswaan", "Dagri", "BAAK", "Wadir3"];
        return codes.Take(count).Select((code, i) => new RenderParticipant(new RoutingStage(i + 1,
            Guid.Parse($"00000000-0000-0000-0000-{i + 1:000000000000}"), "Demo " + code, code, code), "100000000" + i)).ToArray();
    }

    [Theory]
    [InlineData("proposal", 5)]
    [InlineData("lpj", 5)]
    [InlineData("peminjaman-barang", 6)]
    [InlineData("peminjaman-ruangan", 7)]
    public async Task FourTemplates_RenderRealPdfWithBoundedNonOverlappingSlots(string type, int count)
    {
        var environment = new TemplateTestEnvironment();
        var catalog = new TemplateCatalog(environment);
        var template = (await catalog.GetAllAsync(default)).Single(x => x.TypeId == type);
        var (layout, hash) = await catalog.GetRenderLayoutAsync(template, default);
        var input = new PreviewRenderInput(type, template.TemplateId + ":" + template.Version, hash,
            "Pelatihan Teknologi Mahasiswa", Guid.NewGuid(), null, Fields(template), Participants(count, type), layout);
        var rendered = new PdfSharpLetterTemplateRenderer().Render(input, default);
        var final = new PdfSharpLetterTemplateRenderer().RenderFinal(input, "SGN-00000000000000000000000000000001", default);
        Assert.Equal(rendered.Slots, final.Slots);
        Assert.StartsWith("DRAFT", input.Fields["nomor_surat"]);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(rendered.Bytes, 0, 4));
        Assert.Equal(count, rendered.Slots.Length);
        using var pdf = PdfReader.Open(new MemoryStream(rendered.Bytes), PdfDocumentOpenMode.Import);
        Assert.True(pdf.PageCount >= 2);
        foreach (var slot in rendered.Slots)
        {
            Assert.InRange(slot.PageIndex, 0, pdf.PageCount - 1);
            Assert.True(slot.Width >= 100 && slot.Height >= 100);
            Assert.True(slot.X >= 0 && slot.Y >= 0 && slot.X + slot.Width <= pdf.Pages[slot.PageIndex].Width.Point
                && slot.Y + slot.Height <= pdf.Pages[slot.PageIndex].Height.Point);
            Assert.DoesNotContain(rendered.Slots, other => other != slot && other.PageIndex == slot.PageIndex
                && slot.X < other.X + other.Width && slot.X + slot.Width > other.X
                && slot.Y < other.Y + other.Height && slot.Y + slot.Height > other.Y);
        }
        var output = Path.Combine(environment.ContentRootPath, ".data", "renderer-qa");
        Directory.CreateDirectory(output);
        await File.WriteAllBytesAsync(Path.Combine(output, type + ".pdf"), rendered.Bytes);
        var overlay = new SignIt.Modules.Signatures.Services.PdfSharpOverlayService();
        var qr = new SignIt.Modules.Signatures.Services.QRCoderGenerator().GeneratePng("signit:sig:final-qa");
        var signed = await overlay.OverlaySignaturesAsync(final.Bytes, final.Slots.Select(s =>
            new SignIt.Modules.Signatures.Services.PdfSignatureOverlayItem(s.PageIndex, s.X, s.Y, s.Width, s.Height,
                0, qr, "Demo Penandatangan", s.PositionCode, DateTimeOffset.UtcNow, true)).ToArray(), "SIG-QA-FINAL");
        await File.WriteAllBytesAsync(Path.Combine(output, type + "-final.pdf"), signed);
    }

    [Fact]
    public async Task LongWordsAndMultilineBody_PaginateWithoutDroppingFields()
    {
        var catalog = new TemplateCatalog(new TemplateTestEnvironment());
        var template = (await catalog.GetAllAsync(default)).Single(x => x.TypeId == "proposal");
        var (layout, hash) = await catalog.GetRenderLayoutAsync(template, default);
        var fields = Fields(template);
        fields["latar_belakang"] = new string('W', 4000);
        var renderer = new PdfSharpLetterTemplateRenderer();
        var output = renderer.Render(new("proposal", template.TemplateId + ":" + template.Version, hash,
            "Uji Teks Panjang", Guid.NewGuid(), null, fields, Participants(5), layout), default);
        using var pdf = PdfReader.Open(new MemoryStream(output.Bytes), PdfDocumentOpenMode.Import);
        Assert.True(pdf.PageCount >= 5);
        var directory = Path.Combine(new TemplateTestEnvironment().ContentRootPath, ".data", "renderer-qa");
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "long-text.pdf"), output.Bytes);
    }

    [Fact]
    public void JobLease_RejectsStaleActorAndSupportsBoundedRetry()
    {
        var now = DateTimeOffset.UtcNow;
        var job = LetterPreviewJob.Queue(Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), "{}", now);
        var lease = job.Claim(now, TimeSpan.FromSeconds(60));
        Assert.Throws<InvalidOperationException>(() => job.Claim(now, TimeSpan.FromSeconds(60)));
        Assert.Throws<InvalidOperationException>(() => job.Complete(Guid.NewGuid(), Guid.NewGuid(), "[]", now));
        job.Fail(lease, "preview_render_failed", now);
        job.Retry(now);
        lease = job.Claim(now, TimeSpan.FromSeconds(60));
        job.Fail(lease, "preview_render_failed", now);
        job.Retry(now);
        lease = job.Claim(now, TimeSpan.FromSeconds(60));
        job.Fail(lease, "preview_render_failed", now);
        Assert.Throws<InvalidOperationException>(() => job.Retry(now));
    }

    [Fact]
    public async Task UnknownLayoutFieldAndCancellation_AreRejected()
    {
        var catalog = new TemplateCatalog(new TemplateTestEnvironment());
        var template = (await catalog.GetAllAsync(default)).First();
        var (layout, hash) = await catalog.GetRenderLayoutAsync(template, default);
        var input = new PreviewRenderInput(template.TypeId, template.TemplateId + ":" + template.Version, hash,
            "Uji", Guid.NewGuid(), null, Fields(template), Participants(5), layout);
        var renderer = new PdfSharpLetterTemplateRenderer();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => renderer.Render(input, cancelled.Token));
        Assert.Throws<InvalidOperationException>(() => renderer.Render(input with
        { Layout = new(PdfSharpLetterTemplateRenderer.Version, null, [new("paragraph", "{{unknown}}")]) }, default));
    }

    [Fact]
    public async Task LongParticipantIdentity_WrapsAndKeepsSlotsInPageBounds()
    {
        var catalog = new TemplateCatalog(new TemplateTestEnvironment());
        var template = (await catalog.GetAllAsync(default)).First();
        var (layout, hash) = await catalog.GetRenderLayoutAsync(template, default);
        var participants = Participants(7).Select(p => p with
        { Stage = p.Stage with { Name = new string('W', 150), PositionName = new string('W', 150) }, NimNip = new string('9', 50) }).ToArray();
        var rendered = new PdfSharpLetterTemplateRenderer().Render(new(template.TypeId, template.TemplateId + ":" + template.Version,
            hash, "Uji Identitas Panjang", Guid.NewGuid(), null, Fields(template), participants, layout), default);
        using var pdf = PdfReader.Open(new MemoryStream(rendered.Bytes), PdfDocumentOpenMode.Import);
        Assert.Equal(7, rendered.Slots.Length);
        Assert.All(rendered.Slots, slot => Assert.True(slot.Y + slot.Height < pdf.Pages[slot.PageIndex].Height.Point - 30));
    }
}
