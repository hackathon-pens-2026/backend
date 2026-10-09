using System.Buffers.Binary;
using System.IO.Compression;

namespace SignIt.Modules.Signatures.Services;

// PDFsharp cannot import QRCoder's 1-bit grayscale PNG. Expand the actual stored
// pixels (including quiet zones), not a regenerated QR payload. Evidence hashes
// always refer to the original asset, never this transient embedding bitmap.
internal static class QrPdfImage
{
    public static byte[] Normalize(byte[] image)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (image.Length < 33 || !image.AsSpan(0, 8).SequenceEqual(signature)) return image;
        if (image[24] != 1 || image[25] != 0) return image;
        var width = BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(20, 4));
        if (width is < 1 or > 2048 || height is < 1 or > 2048
            || image[26] != 0 || image[27] != 0 || image[28] != 0)
            throw new InvalidDataException("Format QR PNG tidak didukung.");
        using var compressed = new MemoryStream();
        var ended = false;
        for (var offset = 8; offset < image.Length;)
        {
            if (image.Length - offset < 12) throw new InvalidDataException("Chunk PNG terpotong.");
            var length = BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(offset, 4));
            if (length < 0 || length > image.Length - offset - 12) throw new InvalidDataException("Ukuran chunk PNG tidak valid.");
            var type = image.AsSpan(offset + 4, 4);
            if (type.SequenceEqual("IDAT"u8)) compressed.Write(image, offset + 8, length);
            if (type.SequenceEqual("IEND"u8)) { ended = true; break; }
            offset += length + 12;
        }
        if (!ended) throw new InvalidDataException("PNG belum lengkap.");
        compressed.Position = 0;
        var rowBytes = (width + 7) / 8;
        var scanlines = new byte[(rowBytes + 1) * height];
        using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress, true))
        {
            zlib.ReadExactly(scanlines);
            if (zlib.ReadByte() != -1) throw new InvalidDataException("Data piksel PNG berlebih.");
        }
        var stride = (width * 3 + 3) & ~3;
        using var output = new MemoryStream(54 + stride * height);
        using var writer = new BinaryWriter(output);
        writer.Write((ushort)0x4D42); writer.Write(54 + stride * height);
        writer.Write(0); writer.Write(54); writer.Write(40);
        writer.Write(width); writer.Write(height); writer.Write((ushort)1); writer.Write((ushort)24);
        writer.Write(0); writer.Write(stride * height); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        var row = new byte[stride];
        for (var y = height - 1; y >= 0; y--)
        {
            var start = y * (rowBytes + 1);
            // QRCoder writes unfiltered scanlines. Reject other filters rather than
            // risk changing a signature's pixels.
            if (scanlines[start] != 0) throw new InvalidDataException("Filter QR PNG tidak didukung.");
            for (var x = 0; x < width; x++)
            {
                var value = (scanlines[start + 1 + x / 8] & (128 >> (x % 8))) == 0 ? (byte)0 : (byte)255;
                row[x * 3] = row[x * 3 + 1] = row[x * 3 + 2] = value;
            }
            writer.Write(row);
        }
        return output.ToArray();
    }
}
