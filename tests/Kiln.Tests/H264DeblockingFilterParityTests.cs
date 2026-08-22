using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Acceptance test for Junior-B-deblock. Verifies the H.264 8.7 in-loop deblocking filter is
/// byte-exact against a spec-derived inline reference across multiple boundary-strength regimes
/// (bS = 0 no-op, bS = 1..3 normal filter, bS = 4 strong filter), at multiple QPs, and at multiple
/// edge orientations. Inline reference avoids a process-based oracle that would silently skip when
/// the FFmpeg binary is unavailable.
/// </summary>
/// <remarks>
/// Tables 8-16 (α), 8-17 (β), and the tC0 lookup are quoted verbatim from H.264 (08/2021); see the
/// constants below. Filter ordering is the spec's left-to-right, top-to-bottom MB raster: vertical
/// edges of an MB first, then horizontal edges, before moving on. Skipping that order produces
/// corner-pixel drift in the second-row-second-column MB that's easy to miss without a fixture.
/// </remarks>
public sealed class H264DeblockingFilterParityTests
{
    private const int Mbs = 2;
    private const int LumaSize = Mbs * 16;
    private const int ChromaSize = Mbs * 8;

    private static readonly byte[] AlphaTable =
    [
          0,   0,   0,   0,   0,   0,   0,   0,   0,   0,
          0,   0,   0,   0,   0,   0,   4,   4,   5,   6,
          7,   8,   9,  10,  12,  13,  15,  17,  20,  22,
         25,  28,  32,  36,  40,  45,  50,  56,  63,  71,
         80,  90, 101, 113, 127, 144, 162, 182, 203, 226,
        255, 255,
    ];

    private static readonly byte[] BetaTable =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 2, 2, 2, 3,
        3, 3, 3, 4, 4, 4, 6, 6, 7, 7,
        8, 8, 9, 9, 10, 10, 11, 11, 12, 12,
        13, 13, 14, 14, 15, 15, 16, 16, 17, 17,
        18, 18,
    ];

    private static readonly byte[,] TC0Table =
    {
        { 0, 0, 0 }, { 0, 0, 0 }, { 0, 0, 0 }, { 0, 0, 0 }, { 0, 0, 0 },
        { 0, 0, 0 }, { 0, 0, 0 }, { 0, 0, 0 }, { 0, 0, 0 }, { 0, 0, 0 },
        { 0, 0, 0 }, { 0, 0, 0 }, { 0, 0, 0 }, { 0, 0, 0 }, { 0, 0, 0 },
        { 0, 0, 0 }, { 0, 0, 0 }, { 0, 0, 1 }, { 0, 0, 1 }, { 0, 0, 1 },
        { 0, 0, 1 }, { 0, 1, 1 }, { 0, 1, 1 }, { 1, 1, 1 }, { 1, 1, 1 },
        { 1, 1, 1 }, { 1, 1, 1 }, { 1, 1, 2 }, { 1, 1, 2 }, { 1, 1, 2 },
        { 1, 1, 2 }, { 1, 2, 3 }, { 1, 2, 3 }, { 2, 2, 3 }, { 2, 2, 4 },
        { 2, 3, 4 }, { 2, 3, 4 }, { 3, 3, 5 }, { 3, 4, 6 }, { 3, 4, 6 },
        { 4, 5, 7 }, { 4, 5, 8 }, { 4, 6, 9 }, { 5, 7, 10 }, { 6, 8, 11 },
        { 6, 8, 13 }, { 7, 10, 14 }, { 8, 11, 16 }, { 9, 12, 18 }, { 10, 13, 20 },
        { 11, 15, 23 }, { 13, 17, 25 },
    };

    private static int Clip3(int lo, int hi, int v) => v < lo ? lo : v > hi ? hi : v;

    private static int IndexA(int qp, int alphaOffsetDiv2) => Clip3(0, 51, qp + 2 * alphaOffsetDiv2);

    private static int IndexB(int qp, int betaOffsetDiv2) => Clip3(0, 51, qp + 2 * betaOffsetDiv2);

    private static void FilterLumaEdgeOne(
        ref byte p3, ref byte p2, ref byte p1, ref byte p0,
        ref byte q0, ref byte q1, ref byte q2, ref byte q3,
        int bS, int qp, int alphaOffsetDiv2, int betaOffsetDiv2)
    {
        if (bS == 0)
        {
            return;
        }

        var alpha = AlphaTable[IndexA(qp, alphaOffsetDiv2)];
        var beta = BetaTable[IndexB(qp, betaOffsetDiv2)];
        if (Math.Abs(p0 - q0) >= alpha || Math.Abs(p1 - p0) >= beta || Math.Abs(q1 - q0) >= beta)
        {
            return;
        }

        var ap = Math.Abs(p2 - p0);
        var aq = Math.Abs(q2 - q0);

        if (bS < 4)
        {
            var tc0 = TC0Table[IndexA(qp, alphaOffsetDiv2), bS - 1];
            var tc = tc0 + (ap < beta ? 1 : 0) + (aq < beta ? 1 : 0);
            var delta = Clip3(-tc, tc, (((q0 - p0) << 2) + (p1 - q1) + 4) >> 3);
            byte newP0 = (byte)Clip3(0, 255, p0 + delta);
            byte newQ0 = (byte)Clip3(0, 255, q0 - delta);
            byte newP1 = p1, newQ1 = q1;
            if (ap < beta)
            {
                newP1 = (byte)(p1 + Clip3(-tc0, tc0, (p2 + ((p0 + q0 + 1) >> 1) - (p1 << 1)) >> 1));
            }

            if (aq < beta)
            {
                newQ1 = (byte)(q1 + Clip3(-tc0, tc0, (q2 + ((p0 + q0 + 1) >> 1) - (q1 << 1)) >> 1));
            }

            p0 = newP0;
            q0 = newQ0;
            p1 = newP1;
            q1 = newQ1;
        }
        else
        {
            var smallGap = Math.Abs(p0 - q0) < ((alpha >> 2) + 2);
            byte newP0, newP1 = p1, newP2 = p2, newQ0, newQ1 = q1, newQ2 = q2;
            if (ap < beta && smallGap)
            {
                newP0 = (byte)((p2 + 2 * p1 + 2 * p0 + 2 * q0 + q1 + 4) >> 3);
                newP1 = (byte)((p2 + p1 + p0 + q0 + 2) >> 2);
                newP2 = (byte)((2 * p3 + 3 * p2 + p1 + p0 + q0 + 4) >> 3);
            }
            else
            {
                newP0 = (byte)((2 * p1 + p0 + q1 + 2) >> 2);
            }

            if (aq < beta && smallGap)
            {
                newQ0 = (byte)((q2 + 2 * q1 + 2 * q0 + 2 * p0 + p1 + 4) >> 3);
                newQ1 = (byte)((q2 + q1 + q0 + p0 + 2) >> 2);
                newQ2 = (byte)((2 * q3 + 3 * q2 + q1 + q0 + p0 + 4) >> 3);
            }
            else
            {
                newQ0 = (byte)((2 * q1 + q0 + p1 + 2) >> 2);
            }

            p0 = newP0;
            p1 = newP1;
            p2 = newP2;
            q0 = newQ0;
            q1 = newQ1;
            q2 = newQ2;
        }
    }

    private static void FilterChromaEdgeOne(
        ref byte p1, ref byte p0, ref byte q0, ref byte q1,
        int bS, int qp, int alphaOffsetDiv2, int betaOffsetDiv2)
    {
        if (bS == 0)
        {
            return;
        }

        var alpha = AlphaTable[IndexA(qp, alphaOffsetDiv2)];
        var beta = BetaTable[IndexB(qp, betaOffsetDiv2)];
        if (Math.Abs(p0 - q0) >= alpha || Math.Abs(p1 - p0) >= beta || Math.Abs(q1 - q0) >= beta)
        {
            return;
        }

        if (bS < 4)
        {
            var tc0 = TC0Table[IndexA(qp, alphaOffsetDiv2), bS - 1];
            var tc = tc0 + 1;
            var delta = Clip3(-tc, tc, (((q0 - p0) << 2) + (p1 - q1) + 4) >> 3);
            p0 = (byte)Clip3(0, 255, p0 + delta);
            q0 = (byte)Clip3(0, 255, q0 - delta);
        }
        else
        {
            p0 = (byte)((2 * p1 + p0 + q1 + 2) >> 2);
            q0 = (byte)((2 * q1 + q0 + p1 + 2) >> 2);
        }
    }

    private static byte[] BuildLumaFixture(int seed)
    {
        var rng = new Random(seed);
        var a = new byte[LumaSize * LumaSize];
        for (var y = 0; y < LumaSize; y++)
        {
            for (var x = 0; x < LumaSize; x++)
            {
                var bias = (x / 16) * 60 + (y / 16) * 30;
                a[y * LumaSize + x] = (byte)Math.Clamp(80 + bias + rng.Next(-15, 16), 0, 255);
            }
        }

        return a;
    }

    private static byte[] BuildChromaFixture(int seed)
    {
        var rng = new Random(seed);
        var a = new byte[ChromaSize * ChromaSize];
        for (var y = 0; y < ChromaSize; y++)
        {
            for (var x = 0; x < ChromaSize; x++)
            {
                var bias = (x / 8) * 50 + (y / 8) * 20;
                a[y * ChromaSize + x] = (byte)Math.Clamp(100 + bias + rng.Next(-10, 11), 0, 255);
            }
        }

        return a;
    }

    /// <summary>Apply the entire reference deblocking pipeline mirroring 8.7.1 ordering: per MB raster, vertical edges then horizontal edges.</summary>
    private static (byte[] y, byte[] u, byte[] v) ReferenceApply(
        byte[] srcY, byte[] srcU, byte[] srcV,
        byte[] bsHorizontal, byte[] bsVertical,
        int[] qpY, int[] qpUv,
        int alphaOffsetDiv2, int betaOffsetDiv2)
    {
        var y = (byte[])srcY.Clone();
        var u = (byte[])srcU.Clone();
        var v = (byte[])srcV.Clone();

        for (var my = 0; my < Mbs; my++)
        {
            for (var mx = 0; mx < Mbs; mx++)
            {
                var mbIndex = my * Mbs + mx;
                FilterMbVerticalLuma(y, mx, my, bsVertical, qpY[mbIndex], alphaOffsetDiv2, betaOffsetDiv2);
                FilterMbVerticalChroma(u, mx, my, bsVertical, qpUv[mbIndex], alphaOffsetDiv2, betaOffsetDiv2);
                FilterMbVerticalChroma(v, mx, my, bsVertical, qpUv[mbIndex], alphaOffsetDiv2, betaOffsetDiv2);

                FilterMbHorizontalLuma(y, mx, my, bsHorizontal, qpY[mbIndex], alphaOffsetDiv2, betaOffsetDiv2);
                FilterMbHorizontalChroma(u, mx, my, bsHorizontal, qpUv[mbIndex], alphaOffsetDiv2, betaOffsetDiv2);
                FilterMbHorizontalChroma(v, mx, my, bsHorizontal, qpUv[mbIndex], alphaOffsetDiv2, betaOffsetDiv2);
            }
        }

        return (y, u, v);
    }

    private static void FilterMbVerticalLuma(
        byte[] y, int mx, int my, byte[] bsVertical, int qp, int aOff, int bOff)
    {
        const int stride = LumaSize;
        for (var ev = 0; ev < 4; ev++)
        {
            if (ev == 0 && mx == 0)
            {
                continue;
            }

            var bsBase = ((my * Mbs + mx) * 16) + ev * 4;
            var px = mx * 16 + ev * 4;
            for (var seg = 0; seg < 4; seg++)
            {
                var bs = bsVertical[bsBase + seg];
                if (bs == 0)
                {
                    continue;
                }

                for (var k = 0; k < 4; k++)
                {
                    var py = my * 16 + seg * 4 + k;
                    var rowOff = py * stride + px;
                    FilterLumaEdgeOne(
                        ref y[rowOff - 4], ref y[rowOff - 3], ref y[rowOff - 2], ref y[rowOff - 1],
                        ref y[rowOff + 0], ref y[rowOff + 1], ref y[rowOff + 2], ref y[rowOff + 3],
                        bs, qp, aOff, bOff);
                }
            }
        }
    }

    private static void FilterMbHorizontalLuma(
        byte[] y, int mx, int my, byte[] bsHorizontal, int qp, int aOff, int bOff)
    {
        const int stride = LumaSize;
        for (var eh = 0; eh < 4; eh++)
        {
            if (eh == 0 && my == 0)
            {
                continue;
            }

            var bsBase = ((my * Mbs + mx) * 16) + eh * 4;
            var pyEdge = my * 16 + eh * 4;
            for (var seg = 0; seg < 4; seg++)
            {
                var bs = bsHorizontal[bsBase + seg];
                if (bs == 0)
                {
                    continue;
                }

                for (var k = 0; k < 4; k++)
                {
                    var px = mx * 16 + seg * 4 + k;
                    FilterLumaEdgeOne(
                        ref y[(pyEdge - 4) * stride + px], ref y[(pyEdge - 3) * stride + px],
                        ref y[(pyEdge - 2) * stride + px], ref y[(pyEdge - 1) * stride + px],
                        ref y[(pyEdge + 0) * stride + px], ref y[(pyEdge + 1) * stride + px],
                        ref y[(pyEdge + 2) * stride + px], ref y[(pyEdge + 3) * stride + px],
                        bs, qp, aOff, bOff);
                }
            }
        }
    }

    private static void FilterMbVerticalChroma(
        byte[] plane, int mx, int my, byte[] bsVertical, int qp, int aOff, int bOff)
    {
        const int stride = ChromaSize;
        for (var ev = 0; ev < 2; ev++)
        {
            if (ev == 0 && mx == 0)
            {
                continue;
            }

            var bsBase = ((my * Mbs + mx) * 16) + ev * 8;
            var px = mx * 8 + ev * 4;
            for (var seg = 0; seg < 4; seg++)
            {
                var bs = bsVertical[bsBase + seg];
                if (bs == 0)
                {
                    continue;
                }

                for (var k = 0; k < 2; k++)
                {
                    var py = my * 8 + seg * 2 + k;
                    var rowOff = py * stride + px;
                    FilterChromaEdgeOne(
                        ref plane[rowOff - 2], ref plane[rowOff - 1],
                        ref plane[rowOff + 0], ref plane[rowOff + 1],
                        bs, qp, aOff, bOff);
                }
            }
        }
    }

    private static void FilterMbHorizontalChroma(
        byte[] plane, int mx, int my, byte[] bsHorizontal, int qp, int aOff, int bOff)
    {
        const int stride = ChromaSize;
        for (var eh = 0; eh < 2; eh++)
        {
            if (eh == 0 && my == 0)
            {
                continue;
            }

            var bsBase = ((my * Mbs + mx) * 16) + eh * 8;
            var pyEdge = my * 8 + eh * 4;
            for (var seg = 0; seg < 4; seg++)
            {
                var bs = bsHorizontal[bsBase + seg];
                if (bs == 0)
                {
                    continue;
                }

                for (var k = 0; k < 2; k++)
                {
                    var px = mx * 8 + seg * 2 + k;
                    FilterChromaEdgeOne(
                        ref plane[(pyEdge - 2) * stride + px], ref plane[(pyEdge - 1) * stride + px],
                        ref plane[(pyEdge + 0) * stride + px], ref plane[(pyEdge + 1) * stride + px],
                        bs, qp, aOff, bOff);
                }
            }
        }
    }

    [Theory]
    [InlineData(28, 1, 0xA1)]
    [InlineData(28, 2, 0xA2)]
    [InlineData(28, 3, 0xA3)]
    [InlineData(28, 4, 0xA4)]
    [InlineData(22, 2, 0xB2)]
    [InlineData(40, 3, 0xC3)]
    public void Apply_uniform_bs_matches_inline_reference(int qp, int bs, int seed)
    {
        var srcY = BuildLumaFixture(seed);
        var srcU = BuildChromaFixture(seed + 1);
        var srcV = BuildChromaFixture(seed + 2);

        const int totalBs = Mbs * Mbs * 16;
        var bsH = new byte[totalBs];
        var bsV = new byte[totalBs];
        Array.Fill(bsH, (byte)bs);
        Array.Fill(bsV, (byte)bs);

        var qpY = new int[Mbs * Mbs];
        var qpUv = new int[Mbs * Mbs];
        Array.Fill(qpY, qp);
        Array.Fill(qpUv, qp);

        var (refY, refU, refV) = ReferenceApply(
            srcY, srcU, srcV, bsH, bsV, qpY, qpUv,
            alphaOffsetDiv2: 0, betaOffsetDiv2: 0);

        var gotY = (byte[])srcY.Clone();
        var gotU = (byte[])srcU.Clone();
        var gotV = (byte[])srcV.Clone();
        H264DeblockingFilter.Apply(
            gotY, LumaSize, gotU, gotV, ChromaSize, Mbs, Mbs, bsH, bsV, qpY, qpUv,
            alphaOffsetDiv2: 0, betaOffsetDiv2: 0);

        for (var i = 0; i < gotY.Length; i++)
        {
            gotY[i].Should().Be(refY[i],
                $"luma sample {i} (y={i / LumaSize}, x={i % LumaSize}) qp={qp} bs={bs} seed={seed:X}");
        }

        for (var i = 0; i < gotU.Length; i++)
        {
            gotU[i].Should().Be(refU[i],
                $"U sample {i} (y={i / ChromaSize}, x={i % ChromaSize}) qp={qp} bs={bs} seed={seed:X}");
            gotV[i].Should().Be(refV[i],
                $"V sample {i} (y={i / ChromaSize}, x={i % ChromaSize}) qp={qp} bs={bs} seed={seed:X}");
        }
    }

    [Fact]
    public void Apply_mixed_chroma_bs_segments_matches_inline_reference()
    {
        var srcY = BuildLumaFixture(seed: 0xD00D);
        var srcU = BuildChromaFixture(seed: 0xD00E);
        var srcV = BuildChromaFixture(seed: 0xD00F);

        const int totalBs = Mbs * Mbs * 16;
        var bsH = new byte[totalBs];
        var bsV = new byte[totalBs];
        for (var i = 0; i < totalBs; i++)
        {
            bsH[i] = (byte)((i + 1) % 5);
            bsV[i] = (byte)((i + 3) % 5);
        }

        var qpY = Enumerable.Repeat(28, Mbs * Mbs).ToArray();
        var qpUv = Enumerable.Repeat(28, Mbs * Mbs).ToArray();

        var (_, refU, refV) = ReferenceApply(
            srcY, srcU, srcV, bsH, bsV, qpY, qpUv,
            alphaOffsetDiv2: 0, betaOffsetDiv2: 0);

        var gotY = (byte[])srcY.Clone();
        var gotU = (byte[])srcU.Clone();
        var gotV = (byte[])srcV.Clone();
        H264DeblockingFilter.Apply(
            gotY, LumaSize, gotU, gotV, ChromaSize, Mbs, Mbs, bsH, bsV, qpY, qpUv,
            alphaOffsetDiv2: 0, betaOffsetDiv2: 0);

        gotU.Should().Equal(refU, "4:2:0 chroma must consume each of the four luma bS segments over two samples.");
        gotV.Should().Equal(refV, "4:2:0 chroma must consume each of the four luma bS segments over two samples.");
    }

    [Fact]
    public void Apply_with_zero_bs_is_an_identity()
    {
        var srcY = BuildLumaFixture(seed: 0xFFFF);
        var srcU = BuildChromaFixture(seed: 0xFFFF + 1);
        var srcV = BuildChromaFixture(seed: 0xFFFF + 2);
        const int totalBs = Mbs * Mbs * 16;
        var bsH = new byte[totalBs];
        var bsV = new byte[totalBs];
        var qpY = Enumerable.Repeat(28, Mbs * Mbs).ToArray();
        var qpUv = Enumerable.Repeat(28, Mbs * Mbs).ToArray();

        var gotY = (byte[])srcY.Clone();
        var gotU = (byte[])srcU.Clone();
        var gotV = (byte[])srcV.Clone();
        H264DeblockingFilter.Apply(
            gotY, LumaSize, gotU, gotV, ChromaSize, Mbs, Mbs, bsH, bsV, qpY, qpUv,
            alphaOffsetDiv2: 0, betaOffsetDiv2: 0);

        gotY.Should().Equal(srcY, "bS = 0 everywhere must be an identity on Y.");
        gotU.Should().Equal(srcU, "bS = 0 everywhere must be an identity on U.");
        gotV.Should().Equal(srcV, "bS = 0 everywhere must be an identity on V.");
    }

    [Fact]
    public void Apply_does_not_filter_picture_top_or_left_boundary()
    {
        var srcY = BuildLumaFixture(seed: 0x1234);
        var srcU = BuildChromaFixture(seed: 0x1235);
        var srcV = BuildChromaFixture(seed: 0x1236);
        const int totalBs = Mbs * Mbs * 16;
        var bsH = new byte[totalBs];
        var bsV = new byte[totalBs];
        Array.Fill(bsH, (byte)2);
        Array.Fill(bsV, (byte)2);
        var qpY = Enumerable.Repeat(28, Mbs * Mbs).ToArray();
        var qpUv = Enumerable.Repeat(28, Mbs * Mbs).ToArray();

        var gotY = (byte[])srcY.Clone();
        var gotU = (byte[])srcU.Clone();
        var gotV = (byte[])srcV.Clone();
        H264DeblockingFilter.Apply(
            gotY, LumaSize, gotU, gotV, ChromaSize, Mbs, Mbs, bsH, bsV, qpY, qpUv,
            alphaOffsetDiv2: 0, betaOffsetDiv2: 0);

        for (var x = 0; x < 4; x++)
        {
            gotY[x].Should().Be(srcY[x],
                "samples in the very first luma row must not be filtered (no MB above the picture).");
        }

        for (var y = 0; y < 4; y++)
        {
            gotY[y * LumaSize].Should().Be(srcY[y * LumaSize],
                "samples in the very first luma column must not be filtered.");
        }
    }
}
