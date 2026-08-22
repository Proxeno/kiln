using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Kiln.Internal.H264;

/// <summary>SIMD broadcast/fill paths for H.264 8.3.1.2 Intra4x4 modes 0..2 (V/H/DC).</summary>
internal static class H264Intra4X4FillSimd
{
    public static bool IsSupported => Sse41.IsSupported || AdvSimd.IsSupported;

    /// <summary>Modes 0..2 of H.264 8.3.1.2 (Vertical, Horizontal, DC); same neighbour layout as
    /// <see cref="H264Intra4X4Prediction.Predict"/>.</summary>
    public static void Predict(
        int mode,
        ReadOnlySpan<byte> topRow,
        ReadOnlySpan<byte> leftCol,
        bool topAvail,
        bool leftAvail,
        Span<byte> dst16)
    {
        if (!IsSupported)
        {
            H264Intra4X4Prediction.Predict(mode, topRow, leftCol, topAvail, leftAvail, dst16);
            return;
        }

        if ((uint)mode > 2u)
        {
            H264Intra4X4Prediction.Predict(mode, topRow, leftCol, topAvail, leftAvail, dst16);
            return;
        }

        ref var d = ref MemoryMarshal.GetReference(dst16);

        switch (mode)
        {
            case 0:
                if (!topAvail)
                {
                    FillDc(topRow, false, leftCol, leftAvail, ref d);
                    return;
                }

                StoreVerticalRow(topRow, ref d);
                return;
            case 1:
                if (!leftAvail)
                {
                    FillDc(topRow, topAvail, leftCol, false, ref d);
                    return;
                }

                StoreHorizontalRows(leftCol, ref d);
                return;
            default:
                FillDc(topRow, topAvail, leftCol, leftAvail, ref d);
                return;
        }
    }

    /// <summary>Match <see cref="H264Intra4X4Prediction"/> vertical tiling: four copies of top T0..T3.</summary>
    private static void StoreVerticalRow(ReadOnlySpan<byte> topRow, ref byte d)
    {
        var v = Vector128.Create(
            topRow[1], topRow[2], topRow[3], topRow[4],
            topRow[1], topRow[2], topRow[3], topRow[4],
            topRow[1], topRow[2], topRow[3], topRow[4],
            topRow[1], topRow[2], topRow[3], topRow[4]);
        v.StoreUnsafe(ref d);
    }

    /// <summary>Match horizontal tiling: each left sample broadcast across one row.</summary>
    private static void StoreHorizontalRows(ReadOnlySpan<byte> leftCol, ref byte d)
    {
        var v = Vector128.Create(
            leftCol[0], leftCol[0], leftCol[0], leftCol[0],
            leftCol[1], leftCol[1], leftCol[1], leftCol[1],
            leftCol[2], leftCol[2], leftCol[2], leftCol[2],
            leftCol[3], leftCol[3], leftCol[3], leftCol[3]);
        v.StoreUnsafe(ref d);
    }

    /// <remarks>Byte-identical to <see cref="H264Intra4X4Prediction"/> DC path.</remarks>
    private static void FillDc(
        ReadOnlySpan<byte> topRow,
        bool topAvail,
        ReadOnlySpan<byte> leftCol,
        bool leftAvail,
        ref byte d)
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
        Vector128.Create(dc).StoreUnsafe(ref d);
    }
}
