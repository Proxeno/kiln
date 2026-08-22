using System.Runtime.CompilerServices;

namespace Kiln.Internal.H264;

/// <summary>4×4 luma intra prediction (H.264 8.3.1.2). All 9 modes implemented.</summary>
internal static class H264Intra4X4Prediction
{
    /// <summary>
    /// Compute one 4×4 prediction block. Sample layout in <paramref name="topRow"/>:
    /// <c>TL</c> at <c>[0]</c>, <c>T0..T7</c> at <c>[1..8]</c> (T4..T7 must already be replicated from T3 by caller
    /// when top-right block isn't reconstructed). <paramref name="leftCol"/>: <c>L0..L3</c> at <c>[0..3]</c>.
    /// </summary>
    public static void Predict(
        int mode,
        ReadOnlySpan<byte> topRow,
        ReadOnlySpan<byte> leftCol,
        bool topAvail,
        bool leftAvail,
        Span<byte> dst16)
    {
        switch (mode)
        {
            case 0: PredictVertical(topRow, topAvail, leftCol, leftAvail, dst16); return;
            case 1: PredictHorizontal(topRow, topAvail, leftCol, leftAvail, dst16); return;
            case 2: PredictDc(topRow, topAvail, leftCol, leftAvail, dst16); return;
            case 3: PredictDiagonalDownLeft(topRow, topAvail, leftCol, leftAvail, dst16); return;
            case 4: PredictDiagonalDownRight(topRow, topAvail, leftCol, leftAvail, dst16); return;
            case 5: PredictVerticalRight(topRow, topAvail, leftCol, leftAvail, dst16); return;
            case 6: PredictHorizontalDown(topRow, topAvail, leftCol, leftAvail, dst16); return;
            case 7: PredictVerticalLeft(topRow, topAvail, leftCol, leftAvail, dst16); return;
            case 8: PredictHorizontalUp(topRow, topAvail, leftCol, leftAvail, dst16); return;
            default: PredictDc(topRow, topAvail, leftCol, leftAvail, dst16); return;
        }
    }

    /// <summary>
    /// Gated dispatcher routing modes 0..2 to <see cref="H264Intra4X4FillSimd"/> (broadcast/fill
    /// helpers) and modes 3..8 to <see cref="H264Intra4X4DirectionalSimd"/> (vector predicts)
    /// when <paramref name="useSimd"/> is set and the corresponding class reports hardware support.
    /// Falls through to scalar when SIMD is unavailable or disabled.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Predict(
        int mode,
        ReadOnlySpan<byte> topRow,
        ReadOnlySpan<byte> leftCol,
        bool topAvail,
        bool leftAvail,
        Span<byte> dst16,
        bool useSimd)
    {
        if (useSimd)
        {
            if ((uint)mode <= 2u)
            {
                if (H264Intra4X4FillSimd.IsSupported)
                {
                    H264Intra4X4FillSimd.Predict(mode, topRow, leftCol, topAvail, leftAvail, dst16);
                    return;
                }
            }
            else if ((uint)(mode - 3) <= 5u && H264Intra4X4DirectionalSimd.IsSupported)
            {
                H264Intra4X4DirectionalSimd.Predict(mode, topRow, leftCol, topAvail, leftAvail, dst16);
                return;
            }
        }

        Predict(mode, topRow, leftCol, topAvail, leftAvail, dst16);
    }

    /// <summary>
    /// SIMD intra 4×4 predict for resolved <see cref="IH264KernelSet"/> tiers. Caller must be a
    /// SIMD tier on a host where fill/directional SIMD is available; no runtime preference or
    /// <c>IsSupported</c> gating.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void PredictSimdDirect(
        int mode,
        ReadOnlySpan<byte> topRow,
        ReadOnlySpan<byte> leftCol,
        bool topAvail,
        bool leftAvail,
        Span<byte> dst16)
    {
        if ((uint)mode <= 2u)
        {
            H264Intra4X4FillSimd.Predict(mode, topRow, leftCol, topAvail, leftAvail, dst16);
            return;
        }

        H264Intra4X4DirectionalSimd.Predict(mode, topRow, leftCol, topAvail, leftAvail, dst16);
    }

    private static void PredictVertical(
        ReadOnlySpan<byte> topRow,
        bool topAvail,
        ReadOnlySpan<byte> leftCol,
        bool leftAvail,
        Span<byte> dst16)
    {
        if (!topAvail)
        {
            PredictDc(topRow, false, leftCol, leftAvail, dst16);
            return;
        }

        for (var r = 0; r < 4; r++)
        {
            for (var c = 0; c < 4; c++)
            {
                dst16[r * 4 + c] = topRow[1 + c];
            }
        }
    }

    private static void PredictHorizontal(
        ReadOnlySpan<byte> topRow,
        bool topAvail,
        ReadOnlySpan<byte> leftCol,
        bool leftAvail,
        Span<byte> dst16)
    {
        if (!leftAvail)
        {
            PredictDc(topRow, topAvail, leftCol, false, dst16);
            return;
        }

        for (var r = 0; r < 4; r++)
        {
            var v = leftCol[r];
            for (var c = 0; c < 4; c++)
            {
                dst16[r * 4 + c] = v;
            }
        }
    }

    private static void PredictDc(
        ReadOnlySpan<byte> topRow,
        bool topAvail,
        ReadOnlySpan<byte> leftCol,
        bool leftAvail,
        Span<byte> dst16)
    {
        var sum = 0;
        var n = 0;
        if (topAvail)
        {
            for (var i = 1; i <= 4; i++)
            {
                sum += topRow[i];
                n++;
            }
        }

        if (leftAvail)
        {
            for (var i = 0; i < 4; i++)
            {
                sum += leftCol[i];
                n++;
            }
        }

        var dc = n == 0 ? (byte)128 : (byte)((sum + (n >> 1)) / n);
        dst16.Fill(dc);
    }

    private static void PredictDiagonalDownLeft(
        ReadOnlySpan<byte> topRow,
        bool topAvail,
        ReadOnlySpan<byte> leftCol,
        bool leftAvail,
        Span<byte> dst16)
    {
        if (!topAvail)
        {
            PredictDc(topRow, false, leftCol, leftAvail, dst16);
            return;
        }

        Span<byte> t = stackalloc byte[8];
        for (var i = 0; i < 8; i++)
        {
            t[i] = topRow[1 + i];
        }

        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                int v;
                if (x == 3 && y == 3)
                {
                    v = (t[6] + 3 * t[7] + 2) >> 2;
                }
                else
                {
                    v = (t[x + y] + 2 * t[x + y + 1] + t[x + y + 2] + 2) >> 2;
                }

                dst16[y * 4 + x] = (byte)v;
            }
        }
    }

    private static void PredictDiagonalDownRight(
        ReadOnlySpan<byte> topRow,
        bool topAvail,
        ReadOnlySpan<byte> leftCol,
        bool leftAvail,
        Span<byte> dst16)
    {
        if (!topAvail || !leftAvail)
        {
            PredictDc(topRow, topAvail, leftCol, leftAvail, dst16);
            return;
        }

        // Build a single 1-D "p" array indexed by H.264 spec offsets.
        // p[-1,-1]=topRow[0], p[0..3,-1]=topRow[1..4], p[-1,0..3]=leftCol[0..3].
        // p[i] for i in [-4..4]: -1 = TL, 0..3 = top, -1..-4 = left top to bottom.
        var tl = topRow[0];
        Span<byte> top = stackalloc byte[4];
        for (var i = 0; i < 4; i++) { top[i] = topRow[1 + i]; }
        Span<byte> left = stackalloc byte[4];
        for (var i = 0; i < 4; i++) { left[i] = leftCol[i]; }

        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                int v;
                if (x > y)
                {
                    var k = x - y;
                    var s2 = k - 2 < 0 ? tl : top[k - 2];
                    var s1 = k - 1 < 0 ? tl : top[k - 1];
                    var s0 = top[k];
                    v = (s2 + 2 * s1 + s0 + 2) >> 2;
                }
                else if (x < y)
                {
                    var k = y - x;
                    var s2 = k - 2 < 0 ? tl : left[k - 2];
                    var s1 = k - 1 < 0 ? tl : left[k - 1];
                    var s0 = left[k];
                    v = (s2 + 2 * s1 + s0 + 2) >> 2;
                }
                else
                {
                    v = (top[0] + 2 * tl + left[0] + 2) >> 2;
                }

                dst16[y * 4 + x] = (byte)v;
            }
        }
    }

    private static void PredictVerticalRight(
        ReadOnlySpan<byte> topRow,
        bool topAvail,
        ReadOnlySpan<byte> leftCol,
        bool leftAvail,
        Span<byte> dst16)
    {
        if (!topAvail || !leftAvail)
        {
            PredictDc(topRow, topAvail, leftCol, leftAvail, dst16);
            return;
        }

        var tl = topRow[0];
        Span<byte> top = stackalloc byte[4];
        for (var i = 0; i < 4; i++) { top[i] = topRow[1 + i]; }
        Span<byte> left = stackalloc byte[4];
        for (var i = 0; i < 4; i++) { left[i] = leftCol[i]; }

        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var zVR = 2 * x - y;
                int v;
                if (zVR == 0 || zVR == 2 || zVR == 4 || zVR == 6)
                {
                    var idx = x - (y >> 1);
                    var a = idx - 1 < 0 ? tl : top[idx - 1];
                    var b = top[idx];
                    v = (a + b + 1) >> 1;
                }
                else if (zVR == 1 || zVR == 3 || zVR == 5)
                {
                    var idx = x - (y >> 1);
                    var a = idx - 2 < 0 ? tl : top[idx - 2];
                    var b = idx - 1 < 0 ? tl : top[idx - 1];
                    var c = top[idx];
                    v = (a + 2 * b + c + 2) >> 2;
                }
                else if (zVR == -1)
                {
                    // H.264 8.3.1.2.6: pred = (p[-1,0] + 2*p[-1,-1] + p[0,-1] + 2) >> 2.
                    v = (left[0] + 2 * tl + top[0] + 2) >> 2;
                }
                else
                {
                    // zVR == -2 (x=0,y=2) or -3 (x=0,y=3): leans into left column.
                    // p[x,y] = (p[-1,y-1] + 2*p[-1,y-2] + p[-1,y-3] + 2) >> 2 (x=0 ⇒ y-2x-k = y-k).
                    var k1 = y - 1;
                    var k2 = y - 2;
                    var k3 = y - 3;
                    var s1 = k1 < 0 ? tl : left[k1];
                    var s2 = k2 < 0 ? tl : left[k2];
                    var s3 = k3 < 0 ? tl : left[k3];
                    v = (s1 + 2 * s2 + s3 + 2) >> 2;
                }

                dst16[y * 4 + x] = (byte)v;
            }
        }
    }

    private static void PredictHorizontalDown(
        ReadOnlySpan<byte> topRow,
        bool topAvail,
        ReadOnlySpan<byte> leftCol,
        bool leftAvail,
        Span<byte> dst16)
    {
        if (!topAvail || !leftAvail)
        {
            PredictDc(topRow, topAvail, leftCol, leftAvail, dst16);
            return;
        }

        var tl = topRow[0];
        Span<byte> top = stackalloc byte[4];
        for (var i = 0; i < 4; i++) { top[i] = topRow[1 + i]; }
        Span<byte> left = stackalloc byte[4];
        for (var i = 0; i < 4; i++) { left[i] = leftCol[i]; }

        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var zHD = 2 * y - x;
                int v;
                if (zHD == 0 || zHD == 2 || zHD == 4 || zHD == 6)
                {
                    var idx = y - (x >> 1);
                    var a = idx - 1 < 0 ? tl : left[idx - 1];
                    var b = left[idx];
                    v = (a + b + 1) >> 1;
                }
                else if (zHD == 1 || zHD == 3 || zHD == 5)
                {
                    var idx = y - (x >> 1);
                    var a = idx - 2 < 0 ? tl : left[idx - 2];
                    var b = idx - 1 < 0 ? tl : left[idx - 1];
                    var c = left[idx];
                    v = (a + 2 * b + c + 2) >> 2;
                }
                else if (zHD == -1)
                {
                    // H.264 8.3.1.2.7: pred = (p[-1,0] + 2*p[-1,-1] + p[0,-1] + 2) >> 2.
                    v = (left[0] + 2 * tl + top[0] + 2) >> 2;
                }
                else
                {
                    // zHD == -2 (x=2,y=0) or -3 (x=3,y=0): leans into top row.
                    // p[x,y] = (p[x-1,-1] + 2*p[x-2,-1] + p[x-3,-1] + 2) >> 2 (y=0 ⇒ x-2y-k = x-k).
                    var k1 = x - 1;
                    var k2 = x - 2;
                    var k3 = x - 3;
                    var s1 = k1 < 0 ? tl : top[k1];
                    var s2 = k2 < 0 ? tl : top[k2];
                    var s3 = k3 < 0 ? tl : top[k3];
                    v = (s1 + 2 * s2 + s3 + 2) >> 2;
                }

                dst16[y * 4 + x] = (byte)v;
            }
        }
    }

    private static void PredictVerticalLeft(
        ReadOnlySpan<byte> topRow,
        bool topAvail,
        ReadOnlySpan<byte> leftCol,
        bool leftAvail,
        Span<byte> dst16)
    {
        if (!topAvail)
        {
            PredictDc(topRow, false, leftCol, leftAvail, dst16);
            return;
        }

        Span<byte> t = stackalloc byte[8];
        for (var i = 0; i < 8; i++) { t[i] = topRow[1 + i]; }

        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                int v;
                if (y == 0 || y == 2)
                {
                    var idx = x + (y >> 1);
                    v = (t[idx] + t[idx + 1] + 1) >> 1;
                }
                else
                {
                    var idx = x + (y >> 1);
                    v = (t[idx] + 2 * t[idx + 1] + t[idx + 2] + 2) >> 2;
                }

                dst16[y * 4 + x] = (byte)v;
            }
        }
    }

    private static void PredictHorizontalUp(
        ReadOnlySpan<byte> topRow,
        bool topAvail,
        ReadOnlySpan<byte> leftCol,
        bool leftAvail,
        Span<byte> dst16)
    {
        if (!leftAvail)
        {
            PredictDc(topRow, topAvail, leftCol, false, dst16);
            return;
        }

        var l0 = leftCol[0];
        var l1 = leftCol[1];
        var l2 = leftCol[2];
        var l3 = leftCol[3];

        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var zHU = x + 2 * y;
                int v;
                if (zHU == 0 || zHU == 2 || zHU == 4)
                {
                    var idx = y + (x >> 1);
                    var a = idx switch { 0 => l0, 1 => l1, 2 => l2, _ => l3 };
                    var b = (idx + 1) switch { 0 => l0, 1 => l1, 2 => l2, _ => l3 };
                    v = (a + b + 1) >> 1;
                }
                else if (zHU == 1 || zHU == 3)
                {
                    var idx = y + (x >> 1);
                    var a = idx switch { 0 => l0, 1 => l1, 2 => l2, _ => l3 };
                    var b = (idx + 1) switch { 0 => l0, 1 => l1, 2 => l2, _ => l3 };
                    var c = (idx + 2) switch { 0 => l0, 1 => l1, 2 => l2, _ => l3 };
                    v = (a + 2 * b + c + 2) >> 2;
                }
                else if (zHU == 5)
                {
                    // H.264 8.3.1.2.9: pred = (p[-1,2] + 3*p[-1,3] + 2) >> 2 for zHU == 5.
                    v = (l2 + 3 * l3 + 2) >> 2;
                }
                else
                {
                    // zHU > 5 (6..9): pred = p[-1,3].
                    v = l3;
                }

                dst16[y * 4 + x] = (byte)v;
            }
        }
    }

    /// <summary>
    /// predIntra4x4PredMode for a 4×4 luma block, per ITU-T H.264 (ISO/IEC 14496-10) clause 8.3.1.1:
    /// <c>Min(intraMxMPredModeA, intraMxMPredModeB)</c>, where 2 (Intra_4x4_DC) stands in for a neighbour
    /// the clause does not treat as carrying an Intra_4x4 / Intra_8x8 mode.
    /// </summary>
    /// <remarks>
    /// Clause 8.3.1.1 sets dcPredModePredictedFlag when either neighbouring macroblock or partition is
    /// unavailable (or, with constrained_intra_pred_flag equal to 1, is coded in an Inter mode), and that
    /// flag forces BOTH intraMxMPredModeA and intraMxMPredModeB to 2 — so whenever it is set the
    /// predicted mode is 2 regardless of the other neighbour. In this encoder the caller's per-block mode
    /// cache stores the sentinel <c>-1</c> for every neighbour of that kind (unavailable, Inter, or
    /// Intra_16x16), which lets the single <c>Min(modeA, modeB) &lt; 0</c> test below stand in for the
    /// flag: a sentinel on either side yields 2, otherwise the plain minimum of the two real modes is
    /// returned. The <c>-1</c> is a convention of this encoder's cache, not a value the specification
    /// defines; populating it correctly is the caller's responsibility.
    /// </remarks>
    internal static int NeighborPredMode(int modeA, int modeB)
    {
        var min = Math.Min(modeA, modeB);
        return min < 0 ? 2 : min;
    }
}
