using System.Buffers.Binary;

namespace Kiln.Capture;

/// <summary>
/// Describes a Windows device-independent bitmap sitting at the front of a captured buffer.
/// </summary>
/// <remarks>
/// Some FlashCap backends — macOS/AVFoundation among them — hand back a complete <c>.bmp</c>
/// image even when transcoding is disabled, rather than the device's packed native format that
/// the characteristics advertise. Sniffing the buffer is the only reliable way to tell, so the
/// converter checks for a DIB before trusting the declared pixel format.
/// </remarks>
internal readonly record struct Dib(int PixelOffset, int Width, int Height, int BytesPerPixel, int Stride, bool BottomUp)
{
    private const int FileHeaderSize = 14;

    /// <summary>Attempts to read a BITMAPFILEHEADER + BITMAPINFOHEADER pair from the buffer.</summary>
    internal static bool TryParse(ReadOnlySpan<byte> buffer, out Dib dib)
    {
        dib = default;

        if (buffer.Length < FileHeaderSize + 40 || buffer[0] != (byte)'B' || buffer[1] != (byte)'M')
        {
            return false;
        }

        var pixelOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[10..]);
        var width = BinaryPrimitives.ReadInt32LittleEndian(buffer[(FileHeaderSize + 4)..]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(buffer[(FileHeaderSize + 8)..]);
        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(buffer[(FileHeaderSize + 14)..]);
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(buffer[(FileHeaderSize + 16)..]);

        // Only uncompressed BI_RGB is worth handling; anything else means a codec we don't have.
        if (compression != 0 || width <= 0 || height == 0 || (bitCount != 24 && bitCount != 32))
        {
            return false;
        }

        var bytesPerPixel = bitCount / 8;

        // DIB rows are padded out to a 4-byte boundary.
        var stride = (((width * bitCount) + 31) / 32) * 4;

        // A positive height means the bottom row is stored first.
        var bottomUp = height > 0;
        var absoluteHeight = Math.Abs(height);

        if (pixelOffset < FileHeaderSize + 40 ||
            (long)pixelOffset + ((long)stride * absoluteHeight) > buffer.Length)
        {
            return false;
        }

        dib = new Dib(pixelOffset, width, absoluteHeight, bytesPerPixel, stride, bottomUp);
        return true;
    }
}
