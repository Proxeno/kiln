using System.Buffers.Binary;
using FlashCap;

namespace Kiln.Capture;

/// <summary>
/// The true layout of the frames a capture device is actually delivering.
/// </summary>
/// <remarks>
/// <para>
/// This has to be discovered rather than assumed. FlashCap backends may return a complete
/// Windows DIB instead of the packed native format the characteristics advertise, and the
/// DIB header is not always truthful: FlashCap 1.12.0's macOS/AVFoundation backend ignores
/// the requested frame size, delivers the device's native resolution, writes the *requested*
/// size into the header, marks the image bottom-up when the rows are actually top-down, and
/// reports <c>RGB32</c> for what is really <c>0RGB</c> (padding byte first).
/// </para>
/// <para>
/// So the header is treated as a hint and cross-checked against the payload length. When the
/// two disagree the header is discarded wholesale — including its orientation claim — and the
/// real geometry is recovered from the payload size, matched against the sizes the device says
/// it supports. Channel order is then probed directly from the pixels.
/// </para>
/// </remarks>
internal sealed record CapturedFrameFormat
{
    private const int FileHeaderSize = 14;
    private const int InfoHeaderSize = 40;

    /// <summary>Byte offset of the first pixel within each delivered buffer.</summary>
    internal required int PixelOffset { get; init; }

    internal required int Width { get; init; }

    internal required int Height { get; init; }

    internal required int BytesPerPixel { get; init; }

    /// <summary>Distance in bytes between the starts of consecutive stored rows.</summary>
    internal required int Stride { get; init; }

    /// <summary>Whether the first stored row is the top row of the image.</summary>
    internal required bool TopDown { get; init; }

    /// <summary>Byte index of the red channel within a pixel; -1 for non-RGB layouts.</summary>
    internal required int RedLane { get; init; }

    internal required int GreenLane { get; init; }

    internal required int BlueLane { get; init; }

    /// <summary>The packed format to decode when this is not an RGB bitmap.</summary>
    internal required PixelFormats PackedFormat { get; init; }

    /// <summary>True when frames arrive as an RGB bitmap rather than a packed YUV format.</summary>
    internal bool IsRgb => RedLane >= 0;

    /// <summary>Set when the delivered geometry did not match what the device advertised.</summary>
    internal required string? Anomaly { get; init; }

    /// <summary>
    /// Works out the real layout from one delivered frame, using the device's advertised sizes
    /// to disambiguate when the header cannot be trusted.
    /// </summary>
    internal static CapturedFrameFormat Resolve(
        ReadOnlySpan<byte> frame,
        PixelFormats declaredFormat,
        int declaredWidth,
        int declaredHeight,
        IReadOnlyList<(int Width, int Height)> candidateSizes)
    {
        ArgumentNullException.ThrowIfNull(candidateSizes);

        if (!TryReadBitmapHeader(frame, out var pixelOffset, out var headerWidth, out var headerHeight,
                out var bytesPerPixel, out var headerBottomUp))
        {
            // Not a bitmap: trust the declared packed format and dimensions.
            return new CapturedFrameFormat
            {
                PixelOffset = 0,
                Width = declaredWidth,
                Height = declaredHeight,
                BytesPerPixel = 0,
                Stride = 0,
                TopDown = true,
                RedLane = -1,
                GreenLane = -1,
                BlueLane = -1,
                PackedFormat = declaredFormat,
                Anomaly = null,
            };
        }

        var payload = frame.Length - pixelOffset;
        var pixelCount = payload / bytesPerPixel;

        string? anomaly = null;
        int width;
        int height;
        bool topDown;

        if ((long)headerWidth * headerHeight == pixelCount)
        {
            width = headerWidth;
            height = headerHeight;
            topDown = !headerBottomUp;
        }
        else
        {
            // The header's dimensions are wrong, so none of it can be relied on. Recover the real
            // geometry from the payload size and assume top-down, which is what the backends
            // exhibiting this bug actually deliver.
            var match = candidateSizes
                .Where(size => (long)size.Width * size.Height == pixelCount)
                .Cast<(int Width, int Height)?>()
                .FirstOrDefault();

            if (match is null)
            {
                throw new InvalidOperationException(
                    $"The device delivered {payload} bytes at {bytesPerPixel} bytes/pixel ({pixelCount} pixels), " +
                    $"but its header claims {headerWidth}x{headerHeight} and no advertised size matches. " +
                    "This sample cannot determine the real frame geometry.");
            }

            width = match.Value.Width;
            height = match.Value.Height;
            topDown = true;
            anomaly =
                $"device ignored the requested size: header says {headerWidth}x{headerHeight}, " +
                $"payload is really {width}x{height}";
        }

        var stride = (((width * bytesPerPixel * 8) + 31) / 32) * 4;
        var (red, green, blue) = ProbeChannelOrder(frame[pixelOffset..], bytesPerPixel, stride, width, height);

        return new CapturedFrameFormat
        {
            PixelOffset = pixelOffset,
            Width = width,
            Height = height,
            BytesPerPixel = bytesPerPixel,
            Stride = stride,
            TopDown = topDown,
            RedLane = red,
            GreenLane = green,
            BlueLane = blue,
            PackedFormat = declaredFormat,
            Anomaly = anomaly,
        };
    }

    private static bool TryReadBitmapHeader(
        ReadOnlySpan<byte> frame,
        out int pixelOffset,
        out int width,
        out int height,
        out int bytesPerPixel,
        out bool bottomUp)
    {
        pixelOffset = 0;
        width = 0;
        height = 0;
        bytesPerPixel = 0;
        bottomUp = false;

        if (frame.Length < FileHeaderSize + InfoHeaderSize || frame[0] != (byte)'B' || frame[1] != (byte)'M')
        {
            return false;
        }

        pixelOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(frame[10..]);
        var storedWidth = BinaryPrimitives.ReadInt32LittleEndian(frame[(FileHeaderSize + 4)..]);
        var storedHeight = BinaryPrimitives.ReadInt32LittleEndian(frame[(FileHeaderSize + 8)..]);
        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(frame[(FileHeaderSize + 14)..]);
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(frame[(FileHeaderSize + 16)..]);

        if (compression != 0 || storedWidth <= 0 || storedHeight == 0 || (bitCount != 24 && bitCount != 32))
        {
            return false;
        }

        if (pixelOffset < FileHeaderSize + InfoHeaderSize || pixelOffset >= frame.Length)
        {
            return false;
        }

        width = storedWidth;

        // A positive height declares bottom-up storage.
        height = Math.Abs(storedHeight);
        bottomUp = storedHeight > 0;
        bytesPerPixel = bitCount / 8;
        return true;
    }

    /// <summary>
    /// Determines which byte of each pixel holds which colour, by finding the padding lane.
    /// </summary>
    /// <remarks>
    /// A 32-bit capture has one byte that carries no colour. Whichever lane is constant across a
    /// sample of pixels is that padding byte: leading padding means 0RGB, trailing means BGR0.
    /// 24-bit captures have no padding lane and follow the usual DIB order, B first.
    /// </remarks>
    private static (int Red, int Green, int Blue) ProbeChannelOrder(
        ReadOnlySpan<byte> pixels, int bytesPerPixel, int stride, int width, int height)
    {
        if (bytesPerPixel == 3)
        {
            return (2, 1, 0);
        }

        Span<bool> constant = [true, true, true, true];
        Span<byte> first = [0, 0, 0, 0];
        var initialized = false;

        // Sample sparsely across the frame; a few hundred pixels settle it.
        for (var row = 0; row < height; row += Math.Max(1, height / 16))
        {
            var rowStart = row * stride;
            for (var col = 0; col < width; col += Math.Max(1, width / 16))
            {
                var p = rowStart + (col * bytesPerPixel);
                if (p + bytesPerPixel > pixels.Length)
                {
                    continue;
                }

                if (!initialized)
                {
                    for (var lane = 0; lane < 4; lane++)
                    {
                        first[lane] = pixels[p + lane];
                    }

                    initialized = true;
                    continue;
                }

                for (var lane = 0; lane < 4; lane++)
                {
                    if (pixels[p + lane] != first[lane])
                    {
                        constant[lane] = false;
                    }
                }
            }
        }

        // Leading padding byte => 0RGB. Otherwise assume the DIB-conventional BGR0.
        return constant[0] && !constant[3] ? (1, 2, 3) : (2, 1, 0);
    }

    internal string Describe()
    {
        var layout = IsRgb
            ? $"{BytesPerPixel * 8}-bit RGB (R@{RedLane} G@{GreenLane} B@{BlueLane}, {(TopDown ? "top-down" : "bottom-up")})"
            : PackedFormat.ToString();
        return $"{Width}x{Height} {layout}";
    }
}
