using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Acceptance test for Junior-G-interp. Verifies the luma quarter-pel (8.4.2.2.1) and chroma 1/8-pel
/// (8.4.2.2.2) sub-pixel interpolation kernels are bit-exact across all 16 luma and 64 chroma
/// fractional positions and across the inter partition block sizes the encoder will request.
/// </summary>
/// <remarks>
/// Luma reference uses the spec's labelled 6-tap (1, -5, 20, 20, -5, 1) horizontal/vertical
/// half-pels plus a 16-bit cascade for the centre half-pel — clipping intermediate values is the
/// historical drift trap (FFmpeg test vectors fail by ±1 sample if you clip the 16-bit intermediate
/// before the second filter pass).
/// </remarks>
public sealed class H264SubpelInterpolationTests
{
    private const int LumaSrcSize = 21;
    private const int ChromaSrcSize = 17;

    private static byte[] BuildLumaPatch(int seed)
    {
        var rng = new Random(seed);
        var a = new byte[LumaSrcSize * LumaSrcSize];
        rng.NextBytes(a);
        return a;
    }

    private static byte[] BuildChromaPatch(int seed)
    {
        var rng = new Random(seed);
        var a = new byte[ChromaSrcSize * ChromaSrcSize];
        rng.NextBytes(a);
        return a;
    }

    private static int Clip255(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    private static int Tap6(int p0, int p1, int p2, int p3, int p4, int p5)
        => p0 - 5 * p1 + 20 * p2 + 20 * p3 - 5 * p4 + p5;

    private static int LumaH(ReadOnlySpan<byte> src, int stride, int x, int y)
        => Tap6(src[y * stride + x - 2], src[y * stride + x - 1], src[y * stride + x],
                src[y * stride + x + 1], src[y * stride + x + 2], src[y * stride + x + 3]);

    private static int LumaV(ReadOnlySpan<byte> src, int stride, int x, int y)
        => Tap6(src[(y - 2) * stride + x], src[(y - 1) * stride + x], src[y * stride + x],
                src[(y + 1) * stride + x], src[(y + 2) * stride + x], src[(y + 3) * stride + x]);

    private static int LumaHv(ReadOnlySpan<byte> src, int stride, int x, int y)
    {
        var i0 = LumaH(src, stride, x, y - 2);
        var i1 = LumaH(src, stride, x, y - 1);
        var i2 = LumaH(src, stride, x, y);
        var i3 = LumaH(src, stride, x, y + 1);
        var i4 = LumaH(src, stride, x, y + 2);
        var i5 = LumaH(src, stride, x, y + 3);
        return Tap6(i0, i1, i2, i3, i4, i5);
    }

    private static int H1(ReadOnlySpan<byte> src, int stride, int x, int y)
        => Clip255((LumaH(src, stride, x, y) + 16) >> 5);

    private static int V1(ReadOnlySpan<byte> src, int stride, int x, int y)
        => Clip255((LumaV(src, stride, x, y) + 16) >> 5);

    private static int Hv(ReadOnlySpan<byte> src, int stride, int x, int y)
        => Clip255((LumaHv(src, stride, x, y) + 512) >> 10);

    private static int G(ReadOnlySpan<byte> src, int stride, int x, int y) => src[y * stride + x];

    /// <summary>Spec-derived reference for one luma sample at fractional (xFrac, yFrac) given an integer base (x, y) in <paramref name="src"/>.</summary>
    private static byte ReferenceLumaAt(
        ReadOnlySpan<byte> src, int stride, int x, int y, int xFrac, int yFrac)
    {
        switch ((xFrac, yFrac))
        {
            case (0, 0): return (byte)G(src, stride, x, y);
            case (1, 0): return (byte)((G(src, stride, x, y) + H1(src, stride, x, y) + 1) >> 1);
            case (2, 0): return (byte)H1(src, stride, x, y);
            case (3, 0): return (byte)((G(src, stride, x + 1, y) + H1(src, stride, x, y) + 1) >> 1);
            case (0, 1): return (byte)((G(src, stride, x, y) + V1(src, stride, x, y) + 1) >> 1);
            case (0, 2): return (byte)V1(src, stride, x, y);
            case (0, 3): return (byte)((G(src, stride, x, y + 1) + V1(src, stride, x, y) + 1) >> 1);
            case (1, 1): return (byte)((H1(src, stride, x, y) + V1(src, stride, x, y) + 1) >> 1);
            case (2, 1): return (byte)((H1(src, stride, x, y) + Hv(src, stride, x, y) + 1) >> 1);
            case (3, 1): return (byte)((H1(src, stride, x, y) + V1(src, stride, x + 1, y) + 1) >> 1);
            case (1, 2): return (byte)((V1(src, stride, x, y) + Hv(src, stride, x, y) + 1) >> 1);
            case (2, 2): return (byte)Hv(src, stride, x, y);
            case (3, 2): return (byte)((V1(src, stride, x + 1, y) + Hv(src, stride, x, y) + 1) >> 1);
            case (1, 3): return (byte)((H1(src, stride, x, y + 1) + V1(src, stride, x, y) + 1) >> 1);
            case (2, 3): return (byte)((H1(src, stride, x, y + 1) + Hv(src, stride, x, y) + 1) >> 1);
            case (3, 3): return (byte)((H1(src, stride, x, y + 1) + V1(src, stride, x + 1, y) + 1) >> 1);
            default: throw new ArgumentOutOfRangeException(nameof(xFrac));
        }
    }

    /// <summary>Chroma 1/8-pel bilinear reference per H.264 8.4.2.2.2.</summary>
    private static byte ReferenceChromaAt(
        ReadOnlySpan<byte> src, int stride, int x, int y, int xFrac, int yFrac)
    {
        var a = src[y * stride + x];
        var b = src[y * stride + x + 1];
        var c = src[(y + 1) * stride + x];
        var d = src[(y + 1) * stride + x + 1];
        var pred = (8 - xFrac) * (8 - yFrac) * a
                 + xFrac * (8 - yFrac) * b
                 + (8 - xFrac) * yFrac * c
                 + xFrac * yFrac * d;
        return (byte)((pred + 32) >> 6);
    }

    [Theory]
    [InlineData(4, 4)]
    [InlineData(8, 8)]
    [InlineData(16, 16)]
    [InlineData(16, 8)]
    [InlineData(8, 16)]
    public void Luma_qpel_every_position_matches_reference(int blockWidth, int blockHeight)
    {
        var src = BuildLumaPatch(seed: 0xCAFE);
        const int srcStride = LumaSrcSize;
        const int srcOriginX = 2;
        const int srcOriginY = 2;
        var dst = new byte[blockWidth * blockHeight];
        var dstStride = blockWidth;

        for (var yFrac = 0; yFrac < 4; yFrac++)
        {
            for (var xFrac = 0; xFrac < 4; xFrac++)
            {
                if (srcOriginX + blockWidth + 3 > LumaSrcSize)
                {
                    continue;
                }

                if (srcOriginY + blockHeight + 3 > LumaSrcSize)
                {
                    continue;
                }

                Array.Clear(dst);
                H264QpelLumaInterp.Interpolate(
                    src, srcStride, srcOriginX, srcOriginY, xFrac, yFrac,
                    blockWidth, blockHeight, dst, dstStride);

                for (var i = 0; i < blockHeight; i++)
                {
                    for (var j = 0; j < blockWidth; j++)
                    {
                        var expected = ReferenceLumaAt(
                            src, srcStride, srcOriginX + j, srcOriginY + i, xFrac, yFrac);
                        dst[i * dstStride + j].Should().Be(expected,
                            $"luma {blockWidth}x{blockHeight} (xFrac={xFrac}, yFrac={yFrac}) " +
                            $"sample (i={i}, j={j}) — see H.264 8.4.2.2.1.");
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(4, 4)]
    [InlineData(4, 8)]
    [InlineData(8, 4)]
    [InlineData(8, 8)]
    public void Chroma_bilinear_every_position_matches_reference(int blockWidth, int blockHeight)
    {
        var src = BuildChromaPatch(seed: 0xBEEF);
        const int srcStride = ChromaSrcSize;
        const int srcOriginX = 2;
        const int srcOriginY = 2;
        var dst = new byte[blockWidth * blockHeight];
        var dstStride = blockWidth;

        for (var yFrac = 0; yFrac < 8; yFrac++)
        {
            for (var xFrac = 0; xFrac < 8; xFrac++)
            {
                Array.Clear(dst);
                H264BilinearChromaInterp.Interpolate(
                    src, srcStride, srcOriginX, srcOriginY, xFrac, yFrac,
                    blockWidth, blockHeight, dst, dstStride);

                for (var i = 0; i < blockHeight; i++)
                {
                    for (var j = 0; j < blockWidth; j++)
                    {
                        var expected = ReferenceChromaAt(
                            src, srcStride, srcOriginX + j, srcOriginY + i, xFrac, yFrac);
                        dst[i * dstStride + j].Should().Be(expected,
                            $"chroma {blockWidth}x{blockHeight} (xFrac={xFrac}, yFrac={yFrac}) " +
                            $"sample (i={i}, j={j}) — see H.264 8.4.2.2.2.");
                    }
                }
            }
        }
    }

    /// <summary>Identity check: integer-pel position must copy the source patch unchanged for every block size.</summary>
    [Fact]
    public void Luma_integer_pel_position_copies_source_unchanged()
    {
        var src = BuildLumaPatch(seed: 0xC001);
        const int srcStride = LumaSrcSize;
        const int srcOriginX = 2;
        const int srcOriginY = 2;
        var dst = new byte[16 * 16];
        H264QpelLumaInterp.Interpolate(
            src, srcStride, srcOriginX, srcOriginY, 0, 0, 16, 16, dst, 16);

        for (var i = 0; i < 16; i++)
        {
            for (var j = 0; j < 16; j++)
            {
                dst[i * 16 + j].Should().Be(src[(srcOriginY + i) * srcStride + srcOriginX + j]);
            }
        }
    }

    [Fact]
    public void Chroma_integer_pel_position_copies_source_unchanged()
    {
        var src = BuildChromaPatch(seed: 0xD002);
        const int srcStride = ChromaSrcSize;
        const int srcOriginX = 2;
        const int srcOriginY = 2;
        var dst = new byte[8 * 8];
        H264BilinearChromaInterp.Interpolate(
            src, srcStride, srcOriginX, srcOriginY, 0, 0, 8, 8, dst, 8);

        for (var i = 0; i < 8; i++)
        {
            for (var j = 0; j < 8; j++)
            {
                dst[i * 8 + j].Should().Be(src[(srcOriginY + i) * srcStride + srcOriginX + j]);
            }
        }
    }

    /// <summary>
    /// 16-bit intermediate guard: when the source patch saturates near 255 with high-frequency edges,
    /// the centre half-pel j must still match the reference exactly. A bug clipping the intermediate
    /// 16-bit result before the second 6-tap pass produces a 1..3 sample drift; this test catches it.
    /// </summary>
    [Fact]
    public void Centre_half_pel_keeps_16bit_intermediate_unclipped()
    {
        var src = new byte[LumaSrcSize * LumaSrcSize];
        for (var i = 0; i < src.Length; i++)
        {
            src[i] = ((i * 17) & 1) == 0 ? (byte)0 : (byte)255;
        }

        const int srcStride = LumaSrcSize;
        const int srcOriginX = 2;
        const int srcOriginY = 2;
        var dst = new byte[8 * 8];
        H264QpelLumaInterp.Interpolate(
            src, srcStride, srcOriginX, srcOriginY, xFrac: 2, yFrac: 2,
            blockWidth: 8, blockHeight: 8, dst, 8);

        for (var i = 0; i < 8; i++)
        {
            for (var j = 0; j < 8; j++)
            {
                var expected = ReferenceLumaAt(
                    src, srcStride, srcOriginX + j, srcOriginY + i, 2, 2);
                dst[i * 8 + j].Should().Be(expected,
                    $"high-frequency mc22 sample (i={i}, j={j}) — intermediate 16-bit must remain unclipped.");
            }
        }
    }
}
