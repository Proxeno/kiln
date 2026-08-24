using FlashCap;

namespace Kiln.Capture;

/// <summary>
/// Converts the raw frame formats a camera delivers into the planar I420 that Kiln consumes.
/// </summary>
/// <remarks>
/// Kiln takes three separate 8-bit planes: full-resolution luma plus half-resolution Cb and Cr
/// (see <c>H264BaselineEncoder.EncodeFrame</c>). Cameras hand back packed 4:2:2 or interleaved
/// formats, so every frame needs a repack, and 4:2:2 sources additionally need their chroma
/// subsampled vertically to reach 4:2:0.
/// </remarks>
internal sealed class FrameConverter
{
    private readonly PixelFormats _format;
    private readonly int _width;
    private readonly int _height;

    internal FrameConverter(PixelFormats format, int width, int height)
    {
        if (!IsSupported(format))
        {
            throw new ArgumentException($"Unsupported capture format {format}.", nameof(format));
        }

        _format = format;
        _width = width;
        _height = height;

        Y = new byte[width * height];
        U = new byte[width / 2 * (height / 2)];
        V = new byte[width / 2 * (height / 2)];
    }

    /// <summary>Luma plane, stride <see cref="StrideY"/>.</summary>
    internal byte[] Y { get; }

    /// <summary>Cb plane, stride <see cref="StrideUv"/>.</summary>
    internal byte[] U { get; }

    /// <summary>Cr plane, stride <see cref="StrideUv"/>.</summary>
    internal byte[] V { get; }

    internal int StrideY => _width;

    internal int StrideUv => _width / 2;

    /// <summary>Formats this converter can turn into I420.</summary>
    internal static bool IsSupported(PixelFormats format) => format is
        PixelFormats.YUYV or PixelFormats.UYVY or PixelFormats.NV12 or
        PixelFormats.RGB32 or PixelFormats.ARGB32 or PixelFormats.RGB24;

    /// <summary>Converts one captured frame into <see cref="Y"/>, <see cref="U"/> and <see cref="V"/>.</summary>
    internal void Convert(ReadOnlySpan<byte> source)
    {
        // Some backends return a full DIB regardless of the format the characteristics advertise,
        // so sniff the buffer before trusting _format.
        if (Dib.TryParse(source, out var dib))
        {
            if (dib.Width != _width || dib.Height != _height)
            {
                throw new ArgumentException(
                    $"Captured bitmap is {dib.Width}x{dib.Height} but the encoder expects {_width}x{_height}.",
                    nameof(source));
            }

            ConvertPackedRgb(source[dib.PixelOffset..], dib.Stride, dib.BytesPerPixel, dib.BottomUp);
            return;
        }

        switch (_format)
        {
            case PixelFormats.YUYV:
                // Packed 4:2:2, byte order Y0 Cb Y1 Cr.
                ConvertPackedYuv422(source, lumaOffset: 0, cbOffset: 1, crOffset: 3);
                break;
            case PixelFormats.UYVY:
                // Packed 4:2:2, byte order Cb Y0 Cr Y1.
                ConvertPackedYuv422(source, lumaOffset: 1, cbOffset: 0, crOffset: 2);
                break;
            case PixelFormats.NV12:
                ConvertNv12(source);
                break;
            case PixelFormats.RGB24:
                ConvertPackedRgb(source, _width * 3, bytesPerPixel: 3, bottomUp: false);
                break;
            case PixelFormats.RGB32:
            case PixelFormats.ARGB32:
                ConvertPackedRgb(source, _width * 4, bytesPerPixel: 4, bottomUp: false);
                break;
            default:
                throw new InvalidOperationException($"Unsupported capture format {_format}.");
        }
    }

    /// <summary>
    /// Repacks a packed 4:2:2 frame. Luma is a strided gather; chroma is already horizontally
    /// subsampled, so reaching 4:2:0 only needs the two luma rows of each chroma row averaged.
    /// </summary>
    private void ConvertPackedYuv422(ReadOnlySpan<byte> source, int lumaOffset, int cbOffset, int crOffset)
    {
        var sourceStride = _width * 2;
        RequireLength(source, sourceStride * _height);

        for (var row = 0; row < _height; row++)
        {
            var src = row * sourceStride;
            var dst = row * _width;
            for (var x = 0; x < _width; x++)
            {
                Y[dst + x] = source[src + (x * 2) + lumaOffset];
            }
        }

        var chromaWidth = _width / 2;
        var chromaHeight = _height / 2;
        for (var cy = 0; cy < chromaHeight; cy++)
        {
            var topRow = cy * 2 * sourceStride;
            var bottomRow = topRow + sourceStride;
            var dst = cy * chromaWidth;
            for (var cx = 0; cx < chromaWidth; cx++)
            {
                var top = topRow + (cx * 4);
                var bottom = bottomRow + (cx * 4);
                U[dst + cx] = Average(source[top + cbOffset], source[bottom + cbOffset]);
                V[dst + cx] = Average(source[top + crOffset], source[bottom + crOffset]);
            }
        }
    }

    /// <summary>Splits NV12's interleaved chroma plane into separate Cb and Cr planes.</summary>
    private void ConvertNv12(ReadOnlySpan<byte> source)
    {
        var chromaWidth = _width / 2;
        var chromaHeight = _height / 2;
        RequireLength(source, (_width * _height) + (_width * chromaHeight));

        source[..(_width * _height)].CopyTo(Y);

        var uv = source[(_width * _height)..];
        for (var cy = 0; cy < chromaHeight; cy++)
        {
            var src = cy * _width;
            var dst = cy * chromaWidth;
            for (var cx = 0; cx < chromaWidth; cx++)
            {
                U[dst + cx] = uv[src + (cx * 2)];
                V[dst + cx] = uv[src + (cx * 2) + 1];
            }
        }
    }

    /// <summary>
    /// Converts packed RGB to I420 using the BT.601 studio-swing coefficients, box-averaging each
    /// 2x2 pixel group down to one chroma sample. Byte order follows the DIB convention (B, G, R),
    /// and <paramref name="bottomUp"/> handles DIBs whose first stored row is the bottom one.
    /// </summary>
    private void ConvertPackedRgb(ReadOnlySpan<byte> source, int sourceStride, int bytesPerPixel, bool bottomUp)
    {
        RequireLength(source, sourceStride * _height);

        for (var row = 0; row < _height; row++)
        {
            var src = SourceRow(row, bottomUp) * sourceStride;
            var dst = row * _width;
            for (var x = 0; x < _width; x++)
            {
                var p = src + (x * bytesPerPixel);
                int b = source[p];
                int g = source[p + 1];
                int r = source[p + 2];

                // Y = 16 + (65.738 R + 129.057 G + 25.064 B) / 256, in 16.16 fixed point.
                Y[dst + x] = (byte)(16 + (((66 * r) + (129 * g) + (25 * b) + 128) >> 8));
            }
        }

        var chromaWidth = _width / 2;
        var chromaHeight = _height / 2;
        for (var cy = 0; cy < chromaHeight; cy++)
        {
            var dst = cy * chromaWidth;
            for (var cx = 0; cx < chromaWidth; cx++)
            {
                int sumR = 0, sumG = 0, sumB = 0;
                for (var dy = 0; dy < 2; dy++)
                {
                    var src = SourceRow((cy * 2) + dy, bottomUp) * sourceStride;
                    for (var dx = 0; dx < 2; dx++)
                    {
                        var p = src + (((cx * 2) + dx) * bytesPerPixel);
                        sumB += source[p];
                        sumG += source[p + 1];
                        sumR += source[p + 2];
                    }
                }

                var r = sumR / 4;
                var g = sumG / 4;
                var b = sumB / 4;

                U[dst + cx] = (byte)(128 + (((-38 * r) - (74 * g) + (112 * b) + 128) >> 8));
                V[dst + cx] = (byte)(128 + (((112 * r) - (94 * g) - (18 * b) + 128) >> 8));
            }
        }
    }

    private static byte Average(byte a, byte b) => (byte)((a + b + 1) >> 1);

    /// <summary>Maps an output row to its stored row, accounting for bottom-up DIB ordering.</summary>
    private int SourceRow(int row, bool bottomUp) => bottomUp ? _height - 1 - row : row;

    private static void RequireLength(ReadOnlySpan<byte> source, int required)
    {
        if (source.Length < required)
        {
            throw new ArgumentException(
                $"Captured frame is {source.Length} bytes but the selected format needs {required}.",
                nameof(source));
        }
    }
}
