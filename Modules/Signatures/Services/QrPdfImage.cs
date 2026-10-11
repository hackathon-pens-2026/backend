using System.Buffers.Binary;
using System.IO.Compression;

namespace SignIt.Modules.Signatures.Services;

/// <summary>
/// Normalizes 1-bit grayscale QR PNG images into 24-bit TrueColor Windows BMP format.
/// PDFsharp cannot import QRCoder's 1-bit grayscale PNGs directly.
/// This transient conversion expands raw pixel scanlines without regenerating the QR payload.
/// Cryptographic evidence hashes always reference the original asset, never this transient bitmap.
/// </summary>
internal static class QrPdfImage
{
    // PNG Binary Specification Constants
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private const int PngSignatureLength = 8;
    private const int PngMinValidLength = 33;                // 8-byte signature + 25-byte IHDR chunk
    private const int PngIhdrWidthOffset = 16;
    private const int PngIhdrHeightOffset = 20;
    private const int PngDimensionFieldLength = 4;
    private const int PngIhdrBitDepthOffset = 24;
    private const int PngIhdrColorTypeOffset = 25;
    private const int PngIhdrCompressionOffset = 26;
    private const int PngIhdrFilterOffset = 27;
    private const int PngIhdrInterlaceOffset = 28;

    private const byte PngMonochromeBitDepth = 1;            // 1 bit per pixel
    private const byte PngGrayscaleColorType = 0;            // Grayscale
    private const byte PngStandardCompressionMethod = 0;     // Deflate/Inflate
    private const byte PngStandardFilterMethod = 0;          // Standard adaptive filter
    private const byte PngNoInterlaceMethod = 0;             // Non-interlaced
    private const byte PngFilterTypeNone = 0;                // Filter type 0 (None)

    private const int MinQrDimension = 1;
    private const int MaxQrDimension = 2048;                 // Bounds check against decompression bombs
    private const int PngChunkHeaderLength = 8;              // 4 bytes length + 4 bytes type
    private const int PngChunkCrcLength = 4;
    private const int PngChunkOverhead = PngChunkHeaderLength + PngChunkCrcLength; // 12 bytes
    private const int BitsPerByte = 8;

    // Windows BMP (Bitmap) Specification Constants
    private const ushort BmpMagicHeader = 0x4D42;            // 'BM' in ASCII (Little-Endian)
    private const int BmpFileHeaderSize = 14;
    private const int BmpInfoHeaderSize = 40;
    private const int BmpTotalHeaderSize = BmpFileHeaderSize + BmpInfoHeaderSize; // 54 bytes
    private const ushort BmpColorPlanes = 1;
    private const ushort BmpBitsPerPixel24 = 24;             // 24-bit TrueColor RGB
    private const int BytesPerRgbPixel = 3;
    private const byte BmpPixelBlack = 0;
    private const byte BmpPixelWhite = 255;

    public static byte[] Normalize(byte[] image)
    {
        if (image.Length < PngMinValidLength || !image.AsSpan(0, PngSignatureLength).SequenceEqual(PngSignature))
        {
            return image;
        }

        if (image[PngIhdrBitDepthOffset] != PngMonochromeBitDepth || image[PngIhdrColorTypeOffset] != PngGrayscaleColorType)
        {
            return image;
        }

        var width = BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(PngIhdrWidthOffset, PngDimensionFieldLength));
        var height = BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(PngIhdrHeightOffset, PngDimensionFieldLength));

        if (width < MinQrDimension || width > MaxQrDimension || height < MinQrDimension || height > MaxQrDimension
            || image[PngIhdrCompressionOffset] != PngStandardCompressionMethod
            || image[PngIhdrFilterOffset] != PngStandardFilterMethod
            || image[PngIhdrInterlaceOffset] != PngNoInterlaceMethod)
        {
            throw new InvalidDataException("Format QR PNG tidak didukung.");
        }

        using var compressed = new MemoryStream();
        var ended = false;

        for (var offset = PngSignatureLength; offset < image.Length;)
        {
            if (image.Length - offset < PngChunkOverhead)
            {
                throw new InvalidDataException("Chunk PNG terpotong.");
            }

            var length = BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(offset, PngDimensionFieldLength));
            if (length < 0 || length > image.Length - offset - PngChunkOverhead)
            {
                throw new InvalidDataException("Ukuran chunk PNG tidak valid.");
            }

            var type = image.AsSpan(offset + PngDimensionFieldLength, PngDimensionFieldLength);
            if (type.SequenceEqual("IDAT"u8))
            {
                compressed.Write(image, offset + PngChunkHeaderLength, length);
            }

            if (type.SequenceEqual("IEND"u8))
            {
                ended = true;
                break;
            }

            offset += length + PngChunkOverhead;
        }

        if (!ended)
        {
            throw new InvalidDataException("PNG belum lengkap.");
        }

        compressed.Position = 0;
        var rowBytes = (width + (BitsPerByte - 1)) / BitsPerByte;
        var scanlines = new byte[(rowBytes + 1) * height]; // 1 filter byte prefix per scanline

        using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress, true))
        {
            zlib.ReadExactly(scanlines);
            if (zlib.ReadByte() != -1)
            {
                throw new InvalidDataException("Data piksel PNG berlebih.");
            }
        }

        // BMP rows are padded to multiples of 4 bytes
        var stride = (width * BytesPerRgbPixel + 3) & ~3;
        var totalBmpSize = BmpTotalHeaderSize + (stride * height);

        using var output = new MemoryStream(totalBmpSize);
        using var writer = new BinaryWriter(output);

        // 1. BITMAPFILEHEADER (14 bytes)
        writer.Write(BmpMagicHeader);                    // bfType ('BM')
        writer.Write(totalBmpSize);                      // bfSize
        writer.Write(0);                                 // bfReserved1 & bfReserved2
        writer.Write(BmpTotalHeaderSize);                // bfOffBits (pixel data offset = 54)

        // 2. BITMAPINFOHEADER (40 bytes)
        writer.Write(BmpInfoHeaderSize);                 // biSize (40)
        writer.Write(width);                             // biWidth
        writer.Write(height);                            // biHeight (positive = bottom-up)
        writer.Write(BmpColorPlanes);                    // biPlanes (1)
        writer.Write(BmpBitsPerPixel24);                 // biBitCount (24)
        writer.Write(0);                                 // biCompression (BI_RGB = 0)
        writer.Write(stride * height);                   // biSizeImage
        writer.Write(0);                                 // biXPelsPerMeter
        writer.Write(0);                                 // biYPelsPerMeter
        writer.Write(0);                                 // biClrUsed
        writer.Write(0);                                 // biClrImportant

        // 3. Pixel Data (Bottom-Up raster scan)
        var row = new byte[stride];
        for (var y = height - 1; y >= 0; y--)
        {
            var start = y * (rowBytes + 1);

            // QRCoder writes unfiltered scanlines (Filter 0 = None).
            if (scanlines[start] != PngFilterTypeNone)
            {
                throw new InvalidDataException("Filter QR PNG tidak didukung.");
            }

            for (var x = 0; x < width; x++)
            {
                var mask = 128 >> (x % BitsPerByte);
                var isWhite = (scanlines[start + 1 + (x / BitsPerByte)] & mask) != 0;
                var color = isWhite ? BmpPixelWhite : BmpPixelBlack;

                var pixelOffset = x * BytesPerRgbPixel;
                row[pixelOffset] = color;         // Blue
                row[pixelOffset + 1] = color;     // Green
                row[pixelOffset + 2] = color;     // Red
            }

            writer.Write(row);
        }

        return output.ToArray();
    }
}
