using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SignIt.Infrastructure.Storage;
using SignIt.Modules.Signatures.Services;
using Xunit;

namespace SignIt.Signatures.Tests;

public sealed class QrGeneratorAndStorageTests
{
    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "SignIt.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Fact]
    public void QRCoderGenerator_GeneratesValidPng_WithCorrectMagicBytes()
    {
        var generator = new QRCoderGenerator();
        var bytes = generator.GeneratePng("signit:sig:test12345678");

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 100);
        // PNG magic bytes: 0x89 0x50 0x4E 0x47
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal(0x50, bytes[1]);
        Assert.Equal(0x4E, bytes[2]);
        Assert.Equal(0x47, bytes[3]);

        var hash = generator.ComputeSha256(bytes);
        Assert.Equal(64, hash.Length);

        using var qrGenerator = new QRCoder.QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode("test", QRCoder.QRCodeGenerator.ECCLevel.Q);
        var bmpQr = new QRCoder.BitmapByteQRCode(qrCodeData);
        var bmpBytes = bmpQr.GetGraphic(10);
        Assert.NotNull(bmpBytes);
        Assert.Equal(0x42, bmpBytes[0]); // 'B'
        Assert.Equal(0x4D, bmpBytes[1]); // 'M'

        using var bmpStream = new MemoryStream(bmpBytes);
        var ximage = PdfSharp.Drawing.XImage.FromStream(bmpStream);
        Assert.NotNull(ximage);
    }

    [Fact]
    public async Task LocalStorageService_SaveReadDelete_WorksAsExpected()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "signit_test_" + Guid.NewGuid().ToString("N"));
        var options = Options.Create(new StorageOptions { RootDirectory = tempDir });
        var env = new TestHostEnvironment { ContentRootPath = tempDir };
        var storage = new LocalStorageService(options, env);

        var testBytes = "Hello SignIt"u8.ToArray();
        var path = "signatures/test/file.txt";

        var savedPath = await storage.SaveAsync(path, testBytes, "text/plain", CancellationToken.None);
        Assert.Equal(path, savedPath);

        var exists = await storage.ExistsAsync(path, CancellationToken.None);
        Assert.True(exists);

        var readBytes = await storage.ReadBytesAsync(path, CancellationToken.None);
        Assert.NotNull(readBytes);
        Assert.Equal(testBytes, readBytes);

        await storage.DeleteAsync(path, CancellationToken.None);
        var existsAfterDelete = await storage.ExistsAsync(path, CancellationToken.None);
        Assert.False(existsAfterDelete);

        // Cleanup
        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task LocalStorageService_RejectsDirectoryTraversal()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "signit_test_" + Guid.NewGuid().ToString("N"));
        var options = Options.Create(new StorageOptions { RootDirectory = tempDir });
        var env = new TestHostEnvironment { ContentRootPath = tempDir };
        var storage = new LocalStorageService(options, env);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            storage.SaveAsync("../../malicious.txt", "evil"u8.ToArray(), "text/plain", CancellationToken.None));

        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
    }
}
