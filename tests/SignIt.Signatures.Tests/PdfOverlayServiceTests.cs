using SignIt.Infrastructure.Errors;
using SignIt.Modules.Signatures.Services;
using Xunit;

namespace SignIt.Signatures.Tests;

public sealed class PdfOverlayServiceTests
{
    private readonly PdfSharpOverlayService _service = new();
    private readonly QRCoderGenerator _qr = new();

    [Fact]
    public void GeneratePlaceholderPdf_CreatesValidPdf()
    {
        var bytes = _service.GeneratePlaceholderPdf("Surat Tugas", "SURAT/2026/001", "Isi surat permohonan");

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 200);
        Assert.Equal(0x25, bytes[0]); // %
        Assert.Equal(0x50, bytes[1]); // P
        Assert.Equal(0x44, bytes[2]); // D
        Assert.Equal(0x46, bytes[3]); // F
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OverlaySignatures_WithCompletedAndPendingSlots_RendersSuccessfully(bool png)
    {
        var basePdf = _service.GeneratePlaceholderPdf("Proposal Kegiatan", "PROP/001", "Rencana kegiatan kampus.");
        var qrBytes = png ? _qr.GeneratePng("signit:sig:testuserqr") : _qr.GenerateBmp("signit:sig:testuserqr");

        var items = new List<PdfSignatureOverlayItem>
        {
            new(
                PageIndex: 0,
                X: 50,
                Y: 300,
                Width: 140,
                Height: 120,
                Rotation: 0,
                QrImageBytes: qrBytes,
                SignerName: "Ahmad Ketua",
                SignerPosition: "Ketua Pelaksana",
                SignedAt: DateTimeOffset.UtcNow,
                IsCompleted: true
            ),
            new(
                PageIndex: 0,
                X: 250,
                Y: 300,
                Width: 140,
                Height: 120,
                Rotation: 0,
                QrImageBytes: null,
                SignerName: "Dr. Budi Pembina",
                SignerPosition: "Pembina Organisasi",
                SignedAt: null,
                IsCompleted: false
            )
        };

        var finalPdf = await _service.OverlaySignaturesAsync(basePdf, items, "SIG-2026-ABC");

        Assert.NotNull(finalPdf);
        Assert.True(finalPdf.Length > basePdf.Length);
        Assert.Equal(0x25, finalPdf[0]);
    }

    [Fact]
    public async Task OverlaySignatures_InvalidPdfBytes_ThrowsDomainException()
    {
        var fakeBytes = "Bukan file PDF"u8.ToArray();
        await Assert.ThrowsAsync<SignItDomainException>(() =>
            _service.OverlaySignaturesAsync(fakeBytes, []));
    }
}
