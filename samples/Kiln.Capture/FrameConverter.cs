using FlashCap;

namespace Kiln.Capture;

/// <summary>
/// Converts the raw frames a camera delivers into the planar I420 that Kiln consumes.
/// </summary>
/// <remarks>
/// Kiln takes three separate 8-bit planes: full-resolution luma plus half-resolution Cb and Cr
/// (see <c>H264BaselineEncoder.EncodeFrame</c>). Cameras hand back packed 4:2:2, interleaved or
/// RGB data, so every frame needs a repack, and non-4:2:0 sources also need chroma subsampled.
/// </remarks>
internal sealed class FrameConverter
{
    private readonly CapturedFrameFormat _format;
    private readonly int _width;
    private readonly int _height;

    internal FrameConverter(CapturedFrameFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        if (!format.IsRgb && !IsSupportedPackedFormat(format.PackedFormat))
        {
            throw new ArgumentException(
                $"Unsupported capture format {format.PackedFormat}.", nameof(format));
        }

        _format = format;

        // Kiln needs even dimensions for 4:2:0 chroma, so trim an odd edge row or column.
        _width = format.Width & ~1;
        _height = format.Height & ~1;

        Y = new byte[_width * _height];
        U = new byte[_width / 2 * (_height / 2)];
        V = new byte[_width / 2 * (_height / 2)];
    }

    /// <summary>Luma plane, stride <see cref="StrideY"/>.</summary>
    internal byte[] Y { get; }

    /// <summary>Cb plane, stride <see cref="StrideUv"/>.</summary>
    internal byte[] U { get; }

    /// <summary>Cr plane, stride <see cref="StrideUv"/>.</summary>
    internal byte[] V { get; }

    internal int Width => _width;

    internal int Height => _height;

    internal int StrideY => _width;

    internal int StrideUv => _width / 2;

    /// <summary>Packed formats this converter can turn into I420 when no bitmap header is present.</summary>
    internal static bool IsSupportedPackedFormat(PixelFormats format) => format is
        PixelFormats.YUYV or PixelFormats.UYVY or PixelFormats.NV12;

    /// <summary>Converts one captured frame into <see cref="Y"/>, <see cref="U"/> and <see cref="V"/>.</summary>
    internal void Convert(ReadOnlySpan<byte> source)
    {
        if (_format.IsRgb)
        {
            ConvertRgb(source[_format.PixelOffset..]);
            return;
        }

        switch (_format.PackedFormat)
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
            default:
                throw new InvalidOperationException($"Unsupported capture format {_format.PackedFormat}.");
        }
    }

    /// <summary>
    /// Repacks a packed 4:2:2 frame. Luma is a strided gather; chroma is already horizontally
    /// subsampled, so reaching 4:2:0 only needs the two luma rows of each chroma row averaged.
    /// </summary>
    private void ConvertPackedYuv422(ReadOnlySpan<byte> source, int lumaOffset, int cbOffset, int crOffset)
    {
        var sourceStride = _format.Width * 2;
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
        var sourceStride = _format.Width;
        var chromaWidth = _width / 2;
        var chromaHeight = _height / 2;
        RequireLength(source, sourceStride * (_format.Height + (_format.Height / 2)));

        for (var row = 0; row < _height; row++)
        {
            source.Slice(row * sourceStride, _width).CopyTo(Y.AsSpan(row * _width));
        }

        var uv = source[(sourceStride * _format.Height)..];
        for (var cy = 0; cy < chromaHeight; cy++)
        {
            var src = cy * sourceStride;
            var dst = cy * chromaWidth;
            for (var cx = 0; cx < chromaWidth; cx++)
            {
                U[dst + cx] = uv[src + (cx * 2)];
                V[dst + cx] = uv[src + (cx * 2) + 1];
            }
        }
    }

    /// <summary>
    /// Converts an RGB bitmap to I420 with the BT.601 studio-swing coefficients, box-averaging
    /// each 2x2 pixel group down to one chroma sample.
    /// </summary>
    private void ConvertRgb(ReadOnlySpan<byte> pixels)
    {
        var stride = _format.Stride;
        var bytesPerPixel = _format.BytesPerPixel;
        var red = _format.RedLane;
        var green = _format.GreenLane;
        var blue = _format.BlueLane;
        RequireLength(pixels, stride * _format.Height);

        for (var row = 0; row < _height; row++)
        {
            var src = SourceRow(row) * stride;
            var dst = row * _width;
            for (var x = 0; x < _width; x++)
            {
                var p = src + (x * bytesPerPixel);
                int r = pixels[p + red];
                int g = pixels[p + green];
                int b = pixels[p + blue];

                // Y = 16 + (65.738 R + 129.057 G + 25.064 B) / 256, rounded, in fixed point.
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
                    var src = SourceRow((cy * 2) + dy) * stride;
                    for (var dx = 0; dx < 2; dx++)
                    {
                        var p = src + (((cx * 2) + dx) * bytesPerPixel);
                        sumR += pixels[p + red];
                        sumG += pixels[p + green];
                        sumB += pixels[p + blue];
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

    /// <summary>Maps an output row to its stored row, accounting for bottom-up bitmaps.</summary>
    private int SourceRow(int row) => _format.TopDown ? row : _format.Height - 1 - row;

    private static void RequireLength(ReadOnlySpan<byte> source, int required)
    {
        if (source.Length < required)
        {
            throw new ArgumentException(
                $"Captured frame is {source.Length} bytes but the resolved format needs {required}.",
                nameof(source));
        }
    }
}
