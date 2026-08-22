using FluentAssertions;
using Kiln.Internal.H264;
using Xunit.Abstractions;

namespace Kiln.Tests;

public sealed class H264PSliceSyntaxDiagnosticsTests
{
    private readonly ITestOutputHelper _output;

    public H264PSliceSyntaxDiagnosticsTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Occluding_frame1_p8x16_right_partition_uses_top_right_mvp()
    {
        const int w = 256;
        const int h = 224;
        var ySize = w * h;
        var uvSize = ySize / 4;
        var f0 = BuildOccludingSourceI420(0, w, h);
        var f1 = BuildOccludingSourceI420(1, w, h);
        var headerBitsBySlice = new Dictionary<int, int>();

        H264BaselineSliceEncoder.SliceHeaderBitsHook = (seq, bits, isP) =>
        {
            if (isP)
                headerBitsBySlice[seq] = bits;
        };
        try
        {
            // Pure-inter encode: this test verifies the P_8x16 right-partition top-right MVP path,
            // so the intra-in-P fallback (which would otherwise code the occluded MB as Intra_16×16)
            // is disabled to keep MB16 inter.
            var enc = new H264BaselineSliceEncoder(w, h, qp: 28, lightweightDeblocking: true, enableIntraInPFallback: false);
            _ = enc.EncodeSliceRbsp(
                f0.AsSpan(0, ySize), w,
                f0.AsSpan(ySize, uvSize), f0.AsSpan(ySize + uvSize, uvSize), w / 2,
                isIdr: true, isPslice: false, frameNum: 0, idrPicId: 0, codedFrameIndex: 0).ToArray();

            var rbsp1 = enc.EncodeSliceRbsp(
                f1.AsSpan(0, ySize), w,
                f1.AsSpan(ySize, uvSize), f1.AsSpan(ySize + uvSize, uvSize), w / 2,
                isIdr: false, isPslice: true, frameNum: 1, idrPicId: 1, codedFrameIndex: 1).ToArray();

            var headerBits = headerBitsBySlice.Values.Single();
            var trace = H264PSliceTraceDecoder.Trace(rbsp1, headerBits, w / 16, h / 16);
            var mb16 = trace.Single(t => t.MbIdx == 16);
            var mb18 = trace.Single(t => t.MbIdx == 18);
            _output.WriteLine($"MB16: {mb16.MbTypeDesc}, bits={mb16.AbsBitsBefore}->{mb16.AbsBitsAfter}");
            _output.WriteLine($"MB18: {mb18.MbTypeDesc}, bits={mb18.AbsBitsBefore}->{mb18.AbsBitsAfter}");

            mb16.MbTypeDesc.Should().Contain("P_8x16");
            mb16.MbTypeDesc.Should().Contain("mvd1=(64,0)");
            mb18.MbTypeDesc.Should().Contain("P_16x16");
            mb18.MbTypeDesc.Should().Contain("mvd=(0,0)");
            mb18.MbTypeDesc.Should().Contain("cbp=32");
        }
        finally
        {
            H264BaselineSliceEncoder.SliceHeaderBitsHook = null;
        }
    }

    private static byte[] BuildOccludingSourceI420(int frameIndex, int width, int height)
    {
        var pitch = width * 4;
        var xrgb = new byte[pitch * height];
        FillOccludingBouncingBlock(frameIndex, width, height, xrgb, pitch);
        var i420 = new byte[width * height * 3 / 2];
        Xrgb8888ToI420(xrgb, i420, width, height, pitch);
        return i420;
    }

    private static void FillOccludingBouncingBlock(int frameIndex, int width, int height, Span<byte> pixels, int pitch)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var left = x < width / 2;
                var r = left ? (byte)(60 + (x >> 3)) : (byte)200;
                var g = left ? (byte)80 : (byte)Math.Clamp(220 - (y >> 4), 0, 255);
                var b = left ? (byte)100 : (byte)40;
                WriteXrgb(pixels, pitch, x, y, r, g, b);
            }
        }

        var rearW = Math.Min(40, width / 6);
        var rearH = Math.Min(40, height / 6);
        var rx = frameIndex * 11 % Math.Max(1, width - rearW);
        var ry = (frameIndex * 7 + frameIndex * frameIndex % 91) % Math.Max(1, height - rearH);
        FillRectSolid(pixels, pitch, width, height, rx, ry, rearW, rearH, 255, 0, 255);

        var frontW = Math.Min(48, width / 5);
        var frontH = Math.Min(48, height / 5);
        var fx = (frameIndex * 13 + 19) % Math.Max(1, width - frontW);
        var fy = (frameIndex * 5 + frameIndex / 4) % Math.Max(1, height - frontH);
        FillRectSolid(pixels, pitch, width, height, fx, fy, frontW, frontH, 0, 255, 255);
    }

    private static void FillRectSolid(
        Span<byte> pixels, int pitch, int picW, int picH, int rectX, int rectY, int rectW, int rectH,
        byte r, byte g, byte b)
    {
        var x1 = Math.Clamp(rectX, 0, picW);
        var y1 = Math.Clamp(rectY, 0, picH);
        var x2 = Math.Clamp(rectX + rectW, 0, picW);
        var y2 = Math.Clamp(rectY + rectH, 0, picH);
        for (var y = y1; y < y2; y++)
        for (var x = x1; x < x2; x++)
            WriteXrgb(pixels, pitch, x, y, r, g, b);
    }

    private static void WriteXrgb(Span<byte> pixels, int pitch, int x, int y, byte r, byte g, byte b)
    {
        var i = y * pitch + x * 4;
        pixels[i + 0] = b;
        pixels[i + 1] = g;
        pixels[i + 2] = r;
        pixels[i + 3] = 0;
    }

    private static void Xrgb8888ToI420(ReadOnlySpan<byte> xrgb, Span<byte> i420, int width, int height, int pitch)
    {
        var ySize = width * height;
        var chromaWidth = width / 2;
        var chromaHeight = height / 2;
        var uOffset = ySize;
        var vOffset = ySize + chromaWidth * chromaHeight;

        for (var row = 0; row < height; row++)
        for (var col = 0; col < width; col++)
        {
            var o = row * pitch + col * 4;
            RgbToYuvFixed(xrgb[o + 2], xrgb[o + 1], xrgb[o], out var y, out _, out _);
            i420[col + row * width] = ClampYuvByte(y);
        }

        for (var cy = 0; cy < chromaHeight; cy++)
        for (var cx = 0; cx < chromaWidth; cx++)
        {
            var sumU = 0;
            var sumV = 0;
            for (var dy = 0; dy < 2; dy++)
            for (var dx = 0; dx < 2; dx++)
            {
                var col = cx * 2 + dx;
                var row = cy * 2 + dy;
                var o = row * pitch + col * 4;
                RgbToYuvFixed(xrgb[o + 2], xrgb[o + 1], xrgb[o], out _, out var u, out var v);
                sumU += u;
                sumV += v;
            }

            var uvIndex = cx + cy * chromaWidth;
            i420[uOffset + uvIndex] = ClampYuvByte(sumU / 4);
            i420[vOffset + uvIndex] = ClampYuvByte(sumV / 4);
        }
    }

    private static void RgbToYuvFixed(byte r, byte g, byte b, out int y, out int u, out int v)
    {
        const int yr = 16_843;
        const int yg = 33_030;
        const int yb = 6_416;
        const int ur = -9_714;
        const int ug = -19_071;
        const int ub = 28_785;
        const int vr = 28_785;
        const int vg = -24_103;
        const int vb = -4_682;
        const int round = 32_768;
        y = ((yr * r + yg * g + yb * b + round) >> 16) + 16;
        u = ((ur * r + ug * g + ub * b + round) >> 16) + 128;
        v = ((vr * r + vg * g + vb * b + round) >> 16) + 128;
    }

    private static byte ClampYuvByte(int x) => (byte)(x > 255 ? 255 : x < 0 ? 0 : x);
}
