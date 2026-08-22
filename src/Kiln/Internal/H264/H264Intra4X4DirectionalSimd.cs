using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Kiln.Internal.H264;

/// <summary>SIMD-accelerated directional Intra4×4 predictors (H.264 8.3.1.2 modes 3–8).</summary>
internal static class H264Intra4X4DirectionalSimd
{
    /// <summary>True when an accelerated path is selected for this CPU.</summary>
    public static bool IsSupported => Sse41.IsSupported || AdvSimd.IsSupported;

    /// <summary>
    /// Predict modes 3..8 of H.264 8.3.1.2; same neighbour layout and dst as
    /// <see cref="H264Intra4X4Prediction.Predict"/>.
    /// Modes 0..2 delegate to <see cref="H264Intra4X4Prediction.Predict"/>.
    /// </summary>
    public static void Predict(
        int mode,
        ReadOnlySpan<byte> topRow,
        ReadOnlySpan<byte> leftCol,
        bool topAvail,
        bool leftAvail,
        Span<byte> dst16)
    {
        if (mode is < 3 or > 8)
        {
            H264Intra4X4Prediction.Predict(mode, topRow, leftCol, topAvail, leftAvail, dst16);
            return;
        }

        if (!Sse41.IsSupported && !AdvSimd.IsSupported)
        {
            H264Intra4X4Prediction.Predict(mode, topRow, leftCol, topAvail, leftAvail, dst16);
            return;
        }

        switch (mode)
        {
            case 3:
                PredictDiagonalDownLeftSimd(topRow, leftCol, topAvail, leftAvail, dst16);
                break;
            case 4:
                PredictDiagonalDownRightSimd(topRow, leftCol, topAvail, leftAvail, dst16);
                break;
            case 5:
                PredictVerticalRightSimd(topRow, leftCol, topAvail, leftAvail, dst16);
                break;
            case 6:
                PredictHorizontalDownSimd(topRow, leftCol, topAvail, leftAvail, dst16);
                break;
            case 7:
                PredictVerticalLeftSimd(topRow, leftCol, topAvail, leftAvail, dst16);
                break;
            default:
                PredictHorizontalUpSimd(topRow, leftCol, topAvail, leftAvail, dst16);
                break;
        }
    }

    /// <summary>Diagonal down-left (8.3.1.2.4): tight scalar rows (avoids SIMD construct/extract on 4-wide data).</summary>
    private static void PredictDiagonalDownLeftSimd(
        ReadOnlySpan<byte> topRow,
        ReadOnlySpan<byte> leftCol,
        bool topAvail,
        bool leftAvail,
        Span<byte> dst16)
    {
        if (!topAvail)
        {
            H264Intra4X4Prediction.Predict(2, topRow, leftCol, false, leftAvail, dst16);
            return;
        }

        for (var y = 0; y < 3; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var a = topRow[1 + y + x];
                var b = topRow[2 + y + x];
                var c = topRow[3 + y + x];
                dst16[y * 4 + x] = (byte)((a + b + b + c + 2) >> 2);
            }
        }

        for (var x = 0; x < 3; x++)
        {
            var k = x + 3;
            var v = (topRow[1 + k] + (topRow[2 + k] << 1) + topRow[3 + k] + 2) >> 2;
            dst16[12 + x] = (byte)v;
        }

        dst16[15] = (byte)((topRow[7] + 3 * topRow[8] + 2) >> 2);
    }

    /// <summary>Vertical left (8.3.1.2.8): tight scalar rows.</summary>
    private static void PredictVerticalLeftSimd(
        ReadOnlySpan<byte> topRow,
        ReadOnlySpan<byte> leftCol,
        bool topAvail,
        bool leftAvail,
        Span<byte> dst16)
    {
        if (!topAvail)
        {
            H264Intra4X4Prediction.Predict(2, topRow, leftCol, false, leftAvail, dst16);
            return;
        }

        for (var x = 0; x < 4; x++)
        {
            var a = topRow[1 + x];
            var b = topRow[2 + x];
            dst16[x] = (byte)((a + b + 1) >> 1);
        }

        for (var x = 0; x < 4; x++)
        {
            var a = topRow[1 + x];
            var b = topRow[2 + x];
            var c = topRow[3 + x];
            dst16[4 + x] = (byte)((a + b + b + c + 2) >> 2);
        }

        for (var x = 0; x < 4; x++)
        {
            var a = topRow[2 + x];
            var b = topRow[3 + x];
            dst16[8 + x] = (byte)((a + b + 1) >> 1);
        }

        for (var x = 0; x < 4; x++)
        {
            var a = topRow[2 + x];
            var b = topRow[3 + x];
            var c = topRow[4 + x];
            dst16[12 + x] = (byte)((a + b + b + c + 2) >> 2);
        }
    }

    /// <summary>
    /// Diagonal down-right (8.3.1.2.5): 4×4 grid is a diagonal of identical values from the
    /// top-left corner outward. Each sample is (s2 + 2·s1 + s0 + 2) >> 2 over a 3-tap window
    /// that slides diagonally through topRow (x&gt;y), leftCol (x&lt;y), or the corner (x==y).
    /// </summary>
    private static void PredictDiagonalDownRightSimd(
        ReadOnlySpan<byte> topRow,
        ReadOnlySpan<byte> leftCol,
        bool topAvail,
        bool leftAvail,
        Span<byte> dst16)
    {
        if (!topAvail || !leftAvail)
        {
            H264Intra4X4Prediction.Predict(2, topRow, leftCol, topAvail, leftAvail, dst16);
            return;
        }

        int tl = topRow[0], t1 = topRow[1], t2 = topRow[2], t3 = topRow[3], t4 = topRow[4];
        int l0 = leftCol[0], l1 = leftCol[1], l2 = leftCol[2], l3 = leftCol[3];
        var two = Vector128.Create(2);

        // Row 0: [A, B, C, D] where A=(t1+2*tl+l0)>>2, B=(tl+2*t1+t2)>>2, etc.
        var r0_s2 = Vector128.Create(t1, tl, t1, t2);
        var r0_s1 = Vector128.Create(tl, t1, t2, t3);
        var r0_s0 = Vector128.Create(l0, t2, t3, t4);
        var r0 = (r0_s2 + r0_s1 + r0_s1 + r0_s0 + two) >> 2;
        dst16[0] = (byte)r0.GetElement(0); dst16[1] = (byte)r0.GetElement(1);
        dst16[2] = (byte)r0.GetElement(2); dst16[3] = (byte)r0.GetElement(3);

        // Row 1: [E, A, B, C] — left column enters from E=(tl+2*l0+l1)>>2
        var r1_s2 = Vector128.Create(tl, t1, tl, t1);
        var r1_s1 = Vector128.Create(l0, tl, t1, t2);
        var r1_s0 = Vector128.Create(l1, l0, t2, t3);
        var r1 = (r1_s2 + r1_s1 + r1_s1 + r1_s0 + two) >> 2;
        dst16[4] = (byte)r1.GetElement(0); dst16[5] = (byte)r1.GetElement(1);
        dst16[6] = (byte)r1.GetElement(2); dst16[7] = (byte)r1.GetElement(3);

        // Row 2: [F, E, A, B]
        var r2_s2 = Vector128.Create(l0, tl, t1, tl);
        var r2_s1 = Vector128.Create(l1, l0, tl, t1);
        var r2_s0 = Vector128.Create(l2, l1, l0, t2);
        var r2 = (r2_s2 + r2_s1 + r2_s1 + r2_s0 + two) >> 2;
        dst16[8]  = (byte)r2.GetElement(0); dst16[9]  = (byte)r2.GetElement(1);
        dst16[10] = (byte)r2.GetElement(2); dst16[11] = (byte)r2.GetElement(3);

        // Row 3: [G, F, E, A]
        var r3_s2 = Vector128.Create(l1, l0, tl, t1);
        var r3_s1 = Vector128.Create(l2, l1, l0, tl);
        var r3_s0 = Vector128.Create(l3, l2, l1, l0);
        var r3 = (r3_s2 + r3_s1 + r3_s1 + r3_s0 + two) >> 2;
        dst16[12] = (byte)r3.GetElement(0); dst16[13] = (byte)r3.GetElement(1);
        dst16[14] = (byte)r3.GetElement(2); dst16[15] = (byte)r3.GetElement(3);
    }

    /// <summary>
    /// Vertical right (8.3.1.2.6): even-zVR positions use (a+b+1)>>1 (round-average of two
    /// adjacent top samples), odd-zVR use (a+2b+c+2)>>2 (3-tap). Left column fills the bottom-left
    /// corner where zVR&lt;0.
    /// </summary>
    private static void PredictVerticalRightSimd(
        ReadOnlySpan<byte> topRow,
        ReadOnlySpan<byte> leftCol,
        bool topAvail,
        bool leftAvail,
        Span<byte> dst16)
    {
        if (!topAvail || !leftAvail)
        {
            H264Intra4X4Prediction.Predict(2, topRow, leftCol, topAvail, leftAvail, dst16);
            return;
        }

        int tl = topRow[0], t1 = topRow[1], t2 = topRow[2], t3 = topRow[3], t4 = topRow[4];
        int l0 = leftCol[0], l1 = leftCol[1], l2 = leftCol[2];
        var one = Vector128.Create(1);
        var two = Vector128.Create(2);

        // Row 0: (a+b+1)>>1, a=[tl,t1,t2,t3], b=[t1,t2,t3,t4]
        var r0_a = Vector128.Create(tl, t1, t2, t3);
        var r0_b = Vector128.Create(t1, t2, t3, t4);
        var r0 = (r0_a + r0_b + one) >> 1;
        dst16[0] = (byte)r0.GetElement(0); dst16[1] = (byte)r0.GetElement(1);
        dst16[2] = (byte)r0.GetElement(2); dst16[3] = (byte)r0.GetElement(3);

        // Row 1: (s2+2*s1+s0+2)>>2, s2=[l0,tl,t1,t2], s1=[tl,t1,t2,t3], s0=[t1,t2,t3,t4]
        var r1_s2 = Vector128.Create(l0, tl, t1, t2);
        var r1_s1 = Vector128.Create(tl, t1, t2, t3);
        var r1_s0 = Vector128.Create(t1, t2, t3, t4);
        var r1 = (r1_s2 + r1_s1 + r1_s1 + r1_s0 + two) >> 2;
        dst16[4] = (byte)r1.GetElement(0); dst16[5] = (byte)r1.GetElement(1);
        dst16[6] = (byte)r1.GetElement(2); dst16[7] = (byte)r1.GetElement(3);

        // Row 2: [(l1+2*l0+tl+2)>>2, then row0[0..2] (same 2-tap shifted by 1)]
        dst16[8]  = (byte)((l1 + 2 * l0 + tl + 2) >> 2);
        dst16[9]  = (byte)r0.GetElement(0);
        dst16[10] = (byte)r0.GetElement(1);
        dst16[11] = (byte)r0.GetElement(2);

        // Row 3: s2=[l2,l0,tl,t1], s1=[l1,tl,t1,t2], s0=[l0,t1,t2,t3]
        var r3_s2 = Vector128.Create(l2, l0, tl, t1);
        var r3_s1 = Vector128.Create(l1, tl, t1, t2);
        var r3_s0 = Vector128.Create(l0, t1, t2, t3);
        var r3 = (r3_s2 + r3_s1 + r3_s1 + r3_s0 + two) >> 2;
        dst16[12] = (byte)r3.GetElement(0); dst16[13] = (byte)r3.GetElement(1);
        dst16[14] = (byte)r3.GetElement(2); dst16[15] = (byte)r3.GetElement(3);
    }

    /// <summary>
    /// Horizontal down (8.3.1.2.7): transpose of vertical-right. Each row has an even (>>1) and
    /// odd (>>2) column pattern driven by leftCol. Precomputes A[0..3] and B[0..3] vectors once,
    /// then assembles each row from those lanes.
    /// </summary>
    private static void PredictHorizontalDownSimd(
        ReadOnlySpan<byte> topRow,
        ReadOnlySpan<byte> leftCol,
        bool topAvail,
        bool leftAvail,
        Span<byte> dst16)
    {
        if (!topAvail || !leftAvail)
        {
            H264Intra4X4Prediction.Predict(2, topRow, leftCol, topAvail, leftAvail, dst16);
            return;
        }

        int tl = topRow[0], t1 = topRow[1], t2 = topRow[2], t3 = topRow[3];
        int l0 = leftCol[0], l1 = leftCol[1], l2 = leftCol[2], l3 = leftCol[3];
        var one = Vector128.Create(1);
        var two = Vector128.Create(2);

        // A[0..3] = (lc[i-1]+lc[i]+1)>>1, lc[-1]=tl
        var aV = (Vector128.Create(tl, l0, l1, l2) + Vector128.Create(l0, l1, l2, l3) + one) >> 1;

        // B[0..3] = (s2+2*s1+s0+2)>>2 following the diagonal index pattern
        // B[0]=(l0+2*tl+t1), B[1]=(tl+2*l0+l1), B[2]=(l0+2*l1+l2), B[3]=(l1+2*l2+l3)
        var bV_s2 = Vector128.Create(l0, tl, l0, l1);
        var bV_s1 = Vector128.Create(tl, l0, l1, l2);
        var bV_s0 = Vector128.Create(t1, l1, l2, l3);
        var bV = (bV_s2 + bV_s1 + bV_s1 + bV_s0 + two) >> 2;

        // Row 0: [A0, B0, (t2+2*t1+tl+2)>>2, (t3+2*t2+t1+2)>>2]
        dst16[0] = (byte)aV.GetElement(0);
        dst16[1] = (byte)bV.GetElement(0);
        dst16[2] = (byte)((t2 + 2 * t1 + tl + 2) >> 2);
        dst16[3] = (byte)((t3 + 2 * t2 + t1 + 2) >> 2);

        // Row 1: [A1, B1, A0, B0]
        dst16[4] = (byte)aV.GetElement(1); dst16[5] = (byte)bV.GetElement(1);
        dst16[6] = (byte)aV.GetElement(0); dst16[7] = (byte)bV.GetElement(0);

        // Row 2: [A2, B2, A1, B1]
        dst16[8]  = (byte)aV.GetElement(2); dst16[9]  = (byte)bV.GetElement(2);
        dst16[10] = (byte)aV.GetElement(1); dst16[11] = (byte)bV.GetElement(1);

        // Row 3: [A3, B3, A2, B2]
        dst16[12] = (byte)aV.GetElement(3); dst16[13] = (byte)bV.GetElement(3);
        dst16[14] = (byte)aV.GetElement(2); dst16[15] = (byte)bV.GetElement(2);
    }

    /// <summary>
    /// Horizontal up (8.3.1.2.9): rising diagonal through leftCol. zHU=x+2y determines the
    /// formula: even values use (a+b+1)>>1, odd use 3-tap, zHU≥6 clamp to leftCol[3].
    /// </summary>
    private static void PredictHorizontalUpSimd(
        ReadOnlySpan<byte> topRow,
        ReadOnlySpan<byte> leftCol,
        bool topAvail,
        bool leftAvail,
        Span<byte> dst16)
    {
        if (!leftAvail)
        {
            H264Intra4X4Prediction.Predict(2, topRow, leftCol, topAvail, false, dst16);
            return;
        }

        int l0 = leftCol[0], l1 = leftCol[1], l2 = leftCol[2], l3 = leftCol[3];

        // Row 0: zHU = [0,1,2,3] → [(l0+l1+1)>>1, (l0+2*l1+l2+2)>>2, (l1+l2+1)>>1, (l1+2*l2+l3+2)>>2]
        dst16[0] = (byte)((l0 + l1 + 1) >> 1);
        dst16[1] = (byte)((l0 + 2 * l1 + l2 + 2) >> 2);
        dst16[2] = (byte)((l1 + l2 + 1) >> 1);
        dst16[3] = (byte)((l1 + 2 * l2 + l3 + 2) >> 2);

        // Row 1: zHU = [2,3,4,5] → [(l1+l2+1)>>1, (l1+2*l2+l3+2)>>2, (l2+l3+1)>>1, (l2+3*l3+2)>>2]
        dst16[4] = dst16[2];
        dst16[5] = dst16[3];
        dst16[6] = (byte)((l2 + l3 + 1) >> 1);
        dst16[7] = (byte)((l2 + 3 * l3 + 2) >> 2);

        // Row 2: zHU = [4,5,6,7] → [(l2+l3+1)>>1, (l2+3*l3+2)>>2, l3, l3]
        dst16[8]  = dst16[6];
        dst16[9]  = dst16[7];
        dst16[10] = (byte)l3;
        dst16[11] = (byte)l3;

        // Row 3: zHU = [6,7,8,9] → all l3
        dst16[12] = dst16[13] = dst16[14] = dst16[15] = (byte)l3;
    }
}
