using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Kiln.Internal.H264;

/// <summary>
/// H.264 in-loop deblocking filter (8.7). Operates on full reconstructed Y/U/V planes for one slice.
/// </summary>
/// <remarks>
/// The senior pre-computes the per-MB boundary-strength arrays (8.7.2.1) inside the encoder using
/// mb_type / mv / ref-difference rules; the filter only consumes them. The function operates in
/// place: <paramref name="y"/>, <paramref name="u"/>, and <paramref name="v"/> are mutated to the
/// post-filter reference-picture state.
/// </remarks>
internal static class H264DeblockingFilter
{
    /// <summary>Table 8-16 — α as a function of indexA (QP-derived).</summary>
    private static ReadOnlySpan<byte> AlphaTable =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 4, 4, 5, 6,
        7, 8, 9, 10, 12, 13, 15, 17, 20, 22,
        25, 28, 32, 36, 40, 45, 50, 56, 63, 71,
        80, 90, 101, 113, 127, 144, 162, 182, 203, 226,
        255, 255,
    ];

    /// <summary>Table 8-17 — β as a function of indexB (QP-derived).</summary>
    private static ReadOnlySpan<byte> BetaTable =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 2, 2, 2, 3,
        3, 3, 3, 4, 4, 4, 6, 6, 7, 7,
        8, 8, 9, 9, 10, 10, 11, 11, 12, 12,
        13, 13, 14, 14, 15, 15, 16, 16, 17, 17,
        18, 18,
    ];

    /// <summary>Table 8-17 — tC0[indexA, bS − 1] for bS ∈ 1..3 (verbatim from parity test / spec).</summary>
    private static readonly byte[,] Tc0Table =
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

    /// <summary>Apply the H.264 8.7 in-loop deblocking filter in place over one slice's reconstructed planes.</summary>
    /// <param name="y">Y plane (widthY · heightY samples, row-major).</param>
    /// <param name="strideY">Y row stride in samples.</param>
    /// <param name="u">U plane.</param>
    /// <param name="v">V plane.</param>
    /// <param name="strideUv">Chroma row stride in samples.</param>
    /// <param name="mbWidth">Macroblocks per row.</param>
    /// <param name="mbHeight">Macroblocks per column.</param>
    /// <param name="bsHorizontal">Per-MB horizontal-edge boundary strength array; length 16 · mbWidth · mbHeight (4 horizontal edges × 4 segments per MB).</param>
    /// <param name="bsVertical">Per-MB vertical-edge boundary strength array; same layout as <paramref name="bsHorizontal"/>.</param>
    /// <param name="qpY">Per-MB luma QP; length mbWidth · mbHeight.</param>
    /// <param name="qpUv">Per-MB chroma QP; length mbWidth · mbHeight.</param>
    /// <param name="alphaOffsetDiv2">Slice-level alpha offset / 2 (signed).</param>
    /// <param name="betaOffsetDiv2">Slice-level beta offset / 2 (signed).</param>
    public static void Apply(
        Span<byte> y, int strideY,
        Span<byte> u, Span<byte> v, int strideUv,
        int mbWidth, int mbHeight,
        ReadOnlySpan<byte> bsHorizontal,
        ReadOnlySpan<byte> bsVertical,
        ReadOnlySpan<int> qpY,
        ReadOnlySpan<int> qpUv,
        int alphaOffsetDiv2,
        int betaOffsetDiv2) =>
        Apply(y, strideY, u, v, strideUv, mbWidth, mbHeight, bsHorizontal, bsVertical, qpY, qpUv,
            alphaOffsetDiv2, betaOffsetDiv2, useSimd: H264IntrinsicsPreference.UseDeblockSimd);

    internal static void ApplyScalar(
        Span<byte> y, int strideY,
        Span<byte> u, Span<byte> v, int strideUv,
        int mbWidth, int mbHeight,
        ReadOnlySpan<byte> bsHorizontal,
        ReadOnlySpan<byte> bsVertical,
        ReadOnlySpan<int> qpY,
        ReadOnlySpan<int> qpUv,
        int alphaOffsetDiv2,
        int betaOffsetDiv2) =>
        Apply(y, strideY, u, v, strideUv, mbWidth, mbHeight, bsHorizontal, bsVertical, qpY, qpUv,
            alphaOffsetDiv2, betaOffsetDiv2, useSimd: false);

    internal static void ApplySimd(
        Span<byte> y, int strideY,
        Span<byte> u, Span<byte> v, int strideUv,
        int mbWidth, int mbHeight,
        ReadOnlySpan<byte> bsHorizontal,
        ReadOnlySpan<byte> bsVertical,
        ReadOnlySpan<int> qpY,
        ReadOnlySpan<int> qpUv,
        int alphaOffsetDiv2,
        int betaOffsetDiv2) =>
        Apply(y, strideY, u, v, strideUv, mbWidth, mbHeight, bsHorizontal, bsVertical, qpY, qpUv,
            alphaOffsetDiv2, betaOffsetDiv2, useSimd: true);

    private static void Apply(
        Span<byte> y, int strideY,
        Span<byte> u, Span<byte> v, int strideUv,
        int mbWidth, int mbHeight,
        ReadOnlySpan<byte> bsHorizontal,
        ReadOnlySpan<byte> bsVertical,
        ReadOnlySpan<int> qpY,
        ReadOnlySpan<int> qpUv,
        int alphaOffsetDiv2,
        int betaOffsetDiv2,
        bool useSimd)
    {
        var mbCount = mbWidth * mbHeight;
        Debug.Assert(mbWidth > 0 && mbHeight > 0);
        Debug.Assert(bsHorizontal.Length >= 16 * mbCount);
        Debug.Assert(bsVertical.Length >= 16 * mbCount);
        Debug.Assert(qpY.Length >= mbCount);
        Debug.Assert(qpUv.Length >= mbCount);

        var lumaW = mbWidth * 16;
        var lumaH = mbHeight * 16;
        var chromaW = mbWidth * 8;
        var chromaH = mbHeight * 8;
        Debug.Assert(strideY >= lumaW && y.Length >= (lumaH - 1) * strideY + lumaW);
        Debug.Assert(strideUv >= chromaW && u.Length >= (chromaH - 1) * strideUv + chromaW);
        Debug.Assert(v.Length >= (chromaH - 1) * strideUv + chromaW);

        for (var my = 0; my < mbHeight; my++)
        {
            for (var mx = 0; mx < mbWidth; mx++)
            {
                var mbIndex = my * mbWidth + mx;
                var hasVertical = HasAnyActiveBoundaryStrength(bsVertical, mbIndex, firstEdge: mx == 0 ? 1 : 0);
                var hasHorizontal = HasAnyActiveBoundaryStrength(bsHorizontal, mbIndex, firstEdge: my == 0 ? 1 : 0);
                if (!hasVertical && !hasHorizontal)
                {
                    continue;
                }

                if (hasVertical)
                {
                    FilterMbVerticalLuma(y, strideY, mbWidth, mx, my, bsVertical, qpY, mbIndex, alphaOffsetDiv2, betaOffsetDiv2, useSimd);
                    FilterMbVerticalChroma(u, strideUv, mbWidth, mx, my, bsVertical, qpUv, mbIndex, alphaOffsetDiv2, betaOffsetDiv2);
                    FilterMbVerticalChroma(v, strideUv, mbWidth, mx, my, bsVertical, qpUv, mbIndex, alphaOffsetDiv2, betaOffsetDiv2);
                }

                if (hasHorizontal)
                {
                    FilterMbHorizontalLuma(y, strideY, mbWidth, mx, my, bsHorizontal, qpY, mbIndex, alphaOffsetDiv2, betaOffsetDiv2, useSimd);
                    FilterMbHorizontalChroma(u, strideUv, mbWidth, mx, my, bsHorizontal, qpUv, mbIndex, alphaOffsetDiv2, betaOffsetDiv2);
                    FilterMbHorizontalChroma(v, strideUv, mbWidth, mx, my, bsHorizontal, qpUv, mbIndex, alphaOffsetDiv2, betaOffsetDiv2);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasAnyActiveBoundaryStrength(ReadOnlySpan<byte> bs, int mbIndex, int firstEdge)
    {
        var o = (mbIndex * 16) + (firstEdge * 4);
        var end = (mbIndex * 16) + 16;
        for (; o < end; o++)
        {
            if (bs[o] != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>H.264 8.7.2 — derive indexA for α / tC0 tables.</summary>
    private static int IndexA(int qp, int alphaOffsetDiv2)
    {
        return Clip3(0, 51, qp + 2 * alphaOffsetDiv2);
    }

    /// <summary>H.264 8.7.2 — derive indexB for β table.</summary>
    private static int IndexB(int qp, int betaOffsetDiv2)
    {
        return Clip3(0, 51, qp + 2 * betaOffsetDiv2);
    }

    private static int Clip3(int lo, int hi, int v) => v < lo ? lo : v > hi ? hi : v;

    private static byte LookupTc0(int indexA, int bSMinus1) => Tc0Table[indexA, bSMinus1];

    // ── SIMD dispatch helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>Build a per-lane bsMask: 0xFF where bs&gt;0, 0x00 otherwise (4 bytes per segment).</summary>
    private static Vector128<byte> BuildBsMask(byte bs0, byte bs1, byte bs2, byte bs3)
    {
        var vBs = Vector128.Create(bs0, bs0, bs0, bs0, bs1, bs1, bs1, bs1,
                                   bs2, bs2, bs2, bs2, bs3, bs3, bs3, bs3);
        return Vector128.GreaterThan(vBs, Vector128<byte>.Zero);
    }

    /// <summary>Build a per-lane tC0 vector from per-segment bS values (0 for inactive segments).</summary>
    private static Vector128<byte> BuildTc0Vector(int indexA, byte bs0, byte bs1, byte bs2, byte bs3)
    {
        var t0 = (bs0 > 0 && bs0 < 4) ? LookupTc0(indexA, bs0 - 1) : (byte)0;
        var t1 = (bs1 > 0 && bs1 < 4) ? LookupTc0(indexA, bs1 - 1) : (byte)0;
        var t2 = (bs2 > 0 && bs2 < 4) ? LookupTc0(indexA, bs2 - 1) : (byte)0;
        var t3 = (bs3 > 0 && bs3 < 4) ? LookupTc0(indexA, bs3 - 1) : (byte)0;
        return Vector128.Create(t0, t0, t0, t0, t1, t1, t1, t1, t2, t2, t2, t2, t3, t3, t3, t3);
    }

    /// <summary>H.264 8.7.2 — luminance deblocking filter for one 4-sample edge (8.7.2.4 / 8.7.2.5).</summary>
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
            var indexA = IndexA(qp, alphaOffsetDiv2);
            var tc0 = LookupTc0(indexA, bS - 1);
            var tc = tc0 + (ap < beta ? 1 : 0) + (aq < beta ? 1 : 0);
            var delta = Clip3(-tc, tc, (((q0 - p0) << 2) + (p1 - q1) + 4) >> 3);
            var newP0 = (byte)Clip3(0, 255, p0 + delta);
            var newQ0 = (byte)Clip3(0, 255, q0 - delta);
            var newP1 = p1;
            var newQ1 = q1;
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
            return;
        }

        var smallGap = Math.Abs(p0 - q0) < ((alpha >> 2) + 2);
        byte newP0Strong, newP1Strong = p1, newP2Strong = p2, newQ0Strong, newQ1Strong = q1, newQ2Strong = q2;
        if (ap < beta && smallGap)
        {
            newP0Strong = (byte)((p2 + (2 * p1) + (2 * p0) + (2 * q0) + q1 + 4) >> 3);
            newP1Strong = (byte)((p2 + p1 + p0 + q0 + 2) >> 2);
            newP2Strong = (byte)((2 * p3 + (3 * p2) + p1 + p0 + q0 + 4) >> 3);
        }
        else
        {
            newP0Strong = (byte)((2 * p1 + p0 + q1 + 2) >> 2);
        }

        if (aq < beta && smallGap)
        {
            newQ0Strong = (byte)((q2 + (2 * q1) + (2 * q0) + (2 * p0) + p1 + 4) >> 3);
            newQ1Strong = (byte)((q2 + q1 + q0 + p0 + 2) >> 2);
            newQ2Strong = (byte)((2 * q3 + (3 * q2) + q1 + q0 + p0 + 4) >> 3);
        }
        else
        {
            newQ0Strong = (byte)((2 * q1 + q0 + p1 + 2) >> 2);
        }

        p0 = newP0Strong;
        p1 = newP1Strong;
        p2 = newP2Strong;
        q0 = newQ0Strong;
        q1 = newQ1Strong;
        q2 = newQ2Strong;
    }

    /// <summary>H.264 8.7.2 — chroma (4:2:0) deblocking for one 2-sample edge (8.7.2.4 / 8.7.2.5).</summary>
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
            var indexA = IndexA(qp, alphaOffsetDiv2);
            var tc0 = LookupTc0(indexA, bS - 1);
            var tc = tc0 + 1;
            var delta = Clip3(-tc, tc, (((q0 - p0) << 2) + (p1 - q1) + 4) >> 3);
            p0 = (byte)Clip3(0, 255, p0 + delta);
            q0 = (byte)Clip3(0, 255, q0 - delta);
            return;
        }

        p0 = (byte)((2 * p1 + p0 + q1 + 2) >> 2);
        q0 = (byte)((2 * q1 + q0 + p1 + 2) >> 2);
    }

    /// <summary>
    /// H.264 8.7.2.2 — luma qP for one vertical edge: the MB-boundary edge (edge 0 with a left
    /// neighbour) averages the two MBs' QPY (<c>qPav = (QPp + QPq + 1) &gt;&gt; 1</c>); internal
    /// edges use the MB's own QPY. With constant-QP streams the average degenerates to the MB QP,
    /// which is why the historical current-MB-only shortcut only diverged once per-MB rate control
    /// (<c>TargetBitsPerFrame</c>) made neighbouring QPs differ.
    /// </summary>
    private static int LumaQpForVerticalEdge(ReadOnlySpan<int> qpY, int mbIndex, int edgeIndex, int mx)
    {
        var qpCurrent = qpY[mbIndex];
        if (edgeIndex == 0 && mx > 0)
        {
            return (qpY[mbIndex - 1] + qpCurrent + 1) >> 1;
        }

        return qpCurrent;
    }

    /// <summary>H.264 8.7.2.2 — luma qP for one horizontal edge (see <see cref="LumaQpForVerticalEdge"/>).</summary>
    private static int LumaQpForHorizontalEdge(ReadOnlySpan<int> qpY, int mbWidth, int mbIndex, int edgeIndex, int my)
    {
        var qpCurrent = qpY[mbIndex];
        if (edgeIndex == 0 && my > 0)
        {
            return (qpY[mbIndex - mbWidth] + qpCurrent + 1) >> 1;
        }

        return qpCurrent;
    }

    /// <summary>H.264 8.7.1 — vertical luma edges within one MB (left-to-right), picture-left edge skipped.</summary>
    private static void FilterMbVerticalLuma(
        Span<byte> y,
        int stride,
        int mbWidth,
        int mx,
        int my,
        ReadOnlySpan<byte> bsVertical,
        ReadOnlySpan<int> qpY,
        int mbIndex,
        int alphaOffsetDiv2,
        int betaOffsetDiv2,
        bool useSimd)
    {
        for (var ev = 0; ev < 4; ev++)
        {
            if (ev == 0 && mx == 0)
            {
                continue;
            }

            // Per-edge qP (8.7.2.2): the MB-boundary edge averages with the left neighbour's QPY.
            var qp = LumaQpForVerticalEdge(qpY, mbIndex, ev, mx);
            var indexA = IndexA(qp, alphaOffsetDiv2);
            var alpha = AlphaTable[indexA];
            var beta = BetaTable[IndexB(qp, betaOffsetDiv2)];

            var bsBase = (mbIndex * 16) + (ev * 4);
            var px = (mx * 16) + (ev * 4);

            var bs0 = bsVertical[bsBase + 0];
            var bs1 = bsVertical[bsBase + 1];
            var bs2 = bsVertical[bsBase + 2];
            var bs3 = bsVertical[bsBase + 3];

            if ((bs0 | bs1 | bs2 | bs3) == 0)
            {
                continue;
            }

            if (useSimd)
            {
                var anyNormal = (bs0 > 0 && bs0 < 4) || (bs1 > 0 && bs1 < 4) || (bs2 > 0 && bs2 < 4) || (bs3 > 0 && bs3 < 4);
                var anyStrong = bs0 == 4 || bs1 == 4 || bs2 == 4 || bs3 == 4;

                if (!(anyNormal && anyStrong))
                {
                    var bsMask = BuildBsMask(bs0, bs1, bs2, bs3);
                    var mbY = my * 16;
                    if (anyNormal)
                    {
                        H264DeblockingFilterSimd.FilterVertLumaNormal16(y, stride, px, mbY, bsMask, BuildTc0Vector(indexA, bs0, bs1, bs2, bs3), alpha, beta);
                    }
                    else
                    {
                        H264DeblockingFilterSimd.FilterVertLumaStrong16(y, stride, px, mbY, bsMask, alpha, beta);
                    }

                    continue;
                }
            }

            for (var seg = 0; seg < 4; seg++)
            {
                var bs = bsVertical[bsBase + seg];
                if (bs == 0)
                {
                    continue;
                }

                for (var k = 0; k < 4; k++)
                {
                    var py = (my * 16) + (seg * 4) + k;
                    var rowOff = (py * stride) + px;
                    FilterLumaEdgeOne(
                        ref y[rowOff - 4], ref y[rowOff - 3], ref y[rowOff - 2], ref y[rowOff - 1],
                        ref y[rowOff + 0], ref y[rowOff + 1], ref y[rowOff + 2], ref y[rowOff + 3],
                        bs, qp, alphaOffsetDiv2, betaOffsetDiv2);
                }
            }
        }
    }

    /// <summary>H.264 8.7.1 — horizontal luma edges within one MB, picture-top edge skipped.</summary>
    private static void FilterMbHorizontalLuma(
        Span<byte> y,
        int stride,
        int mbWidth,
        int mx,
        int my,
        ReadOnlySpan<byte> bsHorizontal,
        ReadOnlySpan<int> qpY,
        int mbIndex,
        int alphaOffsetDiv2,
        int betaOffsetDiv2,
        bool useSimd)
    {
        for (var eh = 0; eh < 4; eh++)
        {
            if (eh == 0 && my == 0)
            {
                continue;
            }

            // Per-edge qP (8.7.2.2): the MB-boundary edge averages with the top neighbour's QPY.
            var qp = LumaQpForHorizontalEdge(qpY, mbWidth, mbIndex, eh, my);
            var indexA = IndexA(qp, alphaOffsetDiv2);
            var alpha = AlphaTable[indexA];
            var beta = BetaTable[IndexB(qp, betaOffsetDiv2)];

            var bsBase = (mbIndex * 16) + (eh * 4);
            var pyEdge = (my * 16) + (eh * 4);

            var bs0 = bsHorizontal[bsBase + 0];
            var bs1 = bsHorizontal[bsBase + 1];
            var bs2 = bsHorizontal[bsBase + 2];
            var bs3 = bsHorizontal[bsBase + 3];

            if ((bs0 | bs1 | bs2 | bs3) == 0)
            {
                continue;
            }

            if (useSimd)
            {
                var anyNormal = (bs0 > 0 && bs0 < 4) || (bs1 > 0 && bs1 < 4) || (bs2 > 0 && bs2 < 4) || (bs3 > 0 && bs3 < 4);
                var anyStrong = bs0 == 4 || bs1 == 4 || bs2 == 4 || bs3 == 4;

                if (!(anyNormal && anyStrong))
                {
                    var bsMask = BuildBsMask(bs0, bs1, bs2, bs3);
                    var px0 = mx * 16;
                    if (anyNormal)
                    {
                        H264DeblockingFilterSimd.FilterHorizLumaNormal16(y, stride, px0, pyEdge, bsMask, BuildTc0Vector(indexA, bs0, bs1, bs2, bs3), alpha, beta);
                    }
                    else
                    {
                        H264DeblockingFilterSimd.FilterHorizLumaStrong16(y, stride, px0, pyEdge, bsMask, alpha, beta);
                    }

                    continue;
                }
            }

            for (var seg = 0; seg < 4; seg++)
            {
                var bs = bsHorizontal[bsBase + seg];
                if (bs == 0)
                {
                    continue;
                }

                for (var k = 0; k < 4; k++)
                {
                    var px = (mx * 16) + (seg * 4) + k;
                    FilterLumaEdgeOne(
                        ref y[((pyEdge - 4) * stride) + px], ref y[((pyEdge - 3) * stride) + px],
                        ref y[((pyEdge - 2) * stride) + px], ref y[((pyEdge - 1) * stride) + px],
                        ref y[((pyEdge + 0) * stride) + px], ref y[((pyEdge + 1) * stride) + px],
                        ref y[((pyEdge + 2) * stride) + px], ref y[((pyEdge + 3) * stride) + px],
                        bs, qp, alphaOffsetDiv2, betaOffsetDiv2);
                }
            }
        }
    }

    private static int ChromaQpForVerticalEdge(ReadOnlySpan<int> qpUv, int mbIndex, int edgeIndex, int mx)
    {
        var qpCurrent = qpUv[mbIndex];
        if (edgeIndex == 0 && mx > 0)
        {
            return (qpUv[mbIndex - 1] + qpCurrent + 1) >> 1;
        }

        return qpCurrent;
    }

    private static int ChromaQpForHorizontalEdge(ReadOnlySpan<int> qpUv, int mbWidth, int mbIndex, int edgeIndex, int my)
    {
        var qpCurrent = qpUv[mbIndex];
        if (edgeIndex == 0 && my > 0)
        {
            return (qpUv[mbIndex - mbWidth] + qpCurrent + 1) >> 1;
        }

        return qpCurrent;
    }

    /// <summary>H.264 8.7.1 — vertical chroma edges (4:2:0), picture-left edge skipped.</summary>
    private static void FilterMbVerticalChroma(
        Span<byte> plane,
        int stride,
        int mbWidth,
        int mx,
        int my,
        ReadOnlySpan<byte> bsVertical,
        ReadOnlySpan<int> qpUv,
        int mbIndex,
        int alphaOffsetDiv2,
        int betaOffsetDiv2)
    {
        for (var ev = 0; ev < 2; ev++)
        {
            if (ev == 0 && mx == 0)
            {
                continue;
            }

            var qp = ChromaQpForVerticalEdge(qpUv, mbIndex, ev, mx);
            var bsBase = (mbIndex * 16) + (ev * 8);
            var px = (mx * 8) + (ev * 4);
            for (var seg = 0; seg < 4; seg++)
            {
                var bs = bsVertical[bsBase + seg];
                if (bs == 0)
                {
                    continue;
                }

                for (var k = 0; k < 2; k++)
                {
                    var py = (my * 8) + (seg * 2) + k;
                    var rowOff = (py * stride) + px;
                    FilterChromaEdgeOne(
                        ref plane[rowOff - 2], ref plane[rowOff - 1],
                        ref plane[rowOff + 0], ref plane[rowOff + 1],
                        bs, qp, alphaOffsetDiv2, betaOffsetDiv2);
                }
            }
        }
    }

    /// <summary>H.264 8.7.1 — horizontal chroma edges (4:2:0), picture-top edge skipped.</summary>
    private static void FilterMbHorizontalChroma(
        Span<byte> plane,
        int stride,
        int mbWidth,
        int mx,
        int my,
        ReadOnlySpan<byte> bsHorizontal,
        ReadOnlySpan<int> qpUv,
        int mbIndex,
        int alphaOffsetDiv2,
        int betaOffsetDiv2)
    {
        for (var eh = 0; eh < 2; eh++)
        {
            if (eh == 0 && my == 0)
            {
                continue;
            }

            var qp = ChromaQpForHorizontalEdge(qpUv, mbWidth, mbIndex, eh, my);
            var bsBase = (mbIndex * 16) + (eh * 8);
            var pyEdge = (my * 8) + (eh * 4);
            for (var seg = 0; seg < 4; seg++)
            {
                var bs = bsHorizontal[bsBase + seg];
                if (bs == 0)
                {
                    continue;
                }

                for (var k = 0; k < 2; k++)
                {
                    var px = (mx * 8) + (seg * 2) + k;
                    FilterChromaEdgeOne(
                        ref plane[((pyEdge - 2) * stride) + px], ref plane[((pyEdge - 1) * stride) + px],
                        ref plane[((pyEdge + 0) * stride) + px], ref plane[((pyEdge + 1) * stride) + px],
                        bs, qp, alphaOffsetDiv2, betaOffsetDiv2);
                }
            }
        }
    }
}
