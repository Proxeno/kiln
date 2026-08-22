using System.Diagnostics;

namespace Kiln.Internal.H264;

/// <summary>
/// Intra_16x16 luma prediction per H.264 8.3.3 (Vertical, Horizontal, DC, Plane). Bit-exact to the
/// decoder reconstruction path for <see cref="Predict"/>.
/// </summary>
internal static class H264Intra16x16Prediction
{
    /// <summary>
    /// Compute one 16×16 luma prediction block per H.264 8.3.3. Writes 256 samples in raster order.
    /// </summary>
    /// <param name="mode">0 = 8.3.3.1 Vertical, 1 = 8.3.3.2 Horizontal, 2 = 8.3.3.3 DC, 3 = 8.3.3.4 Plane.</param>
    /// <param name="topRow">Row of 16 samples T0..T15 above the MB; ignored when <paramref name="topAvail"/> is false.</param>
    /// <param name="topAvail">True when the top neighbor row is reconstructed.</param>
    /// <param name="leftCol">Column of 16 samples L0..L15 to the left of the MB; ignored when <paramref name="leftAvail"/> is false.</param>
    /// <param name="leftAvail">True when the left neighbor column is reconstructed.</param>
    /// <param name="topLeft">Top-left corner sample at (-1,-1); ignored when <paramref name="topLeftAvail"/> is false.</param>
    /// <param name="topLeftAvail">True when the top-left corner sample is reconstructed (Plane needs it).</param>
    /// <param name="dst256">16×16 destination in raster order; length must be 256.</param>
    public static void Predict(
        int mode,
        ReadOnlySpan<byte> topRow,
        bool topAvail,
        ReadOnlySpan<byte> leftCol,
        bool leftAvail,
        byte topLeft,
        bool topLeftAvail,
        Span<byte> dst256)
    {
        Debug.Assert(dst256.Length == 256);

        if (H264IntrinsicsPreference.UseIntra16x16PredictSimd)
        {
            PredictSimd(mode, topRow, topAvail, leftCol, leftAvail, topLeft, topLeftAvail, dst256);
            return;
        }

        PredictScalar(mode, topRow, topAvail, leftCol, leftAvail, topLeft, topLeftAvail, dst256);
    }

    internal static void PredictSimd(
        int mode,
        ReadOnlySpan<byte> topRow,
        bool topAvail,
        ReadOnlySpan<byte> leftCol,
        bool leftAvail,
        byte topLeft,
        bool topLeftAvail,
        Span<byte> dst256)
    {
        switch (mode)
        {
            case 0:
                Debug.Assert(topAvail);
                H264Intra16x16PredictionSimd.PredictVertical(dst256, topRow);
                return;
            case 1:
                Debug.Assert(leftAvail);
                H264Intra16x16PredictionSimd.PredictHorizontal(dst256, leftCol);
                return;
            case 2:
                H264Intra16x16PredictionSimd.PredictDC(dst256, topRow, topAvail, leftCol, leftAvail);
                return;
            case 3:
                Debug.Assert(topAvail && leftAvail && topLeftAvail);
                H264Intra16x16PredictionSimd.PredictPlane(dst256, topRow, leftCol, topLeft);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    internal static void PredictScalar(
        int mode,
        ReadOnlySpan<byte> topRow,
        bool topAvail,
        ReadOnlySpan<byte> leftCol,
        bool leftAvail,
        byte topLeft,
        bool topLeftAvail,
        Span<byte> dst256)
    {
        switch (mode)
        {
            case 0:
                Debug.Assert(topAvail);
                for (var y = 0; y < 16; y++)
                {
                    for (var x = 0; x < 16; x++)
                    {
                        dst256[y * 16 + x] = topRow[x];
                    }
                }

                return;

            case 1:
                Debug.Assert(leftAvail);
                for (var y = 0; y < 16; y++)
                {
                    var v = leftCol[y];
                    for (var x = 0; x < 16; x++)
                    {
                        dst256[y * 16 + x] = v;
                    }
                }

                return;

            case 2:
            {
                int dc;
                if (topAvail && leftAvail)
                {
                    var s = 0;
                    for (var i = 0; i < 16; i++)
                    {
                        s += topRow[i];
                        s += leftCol[i];
                    }

                    dc = (s + 16) >> 5;
                }
                else if (topAvail)
                {
                    var s = 0;
                    for (var i = 0; i < 16; i++)
                    {
                        s += topRow[i];
                    }

                    dc = (s + 8) >> 4;
                }
                else if (leftAvail)
                {
                    var s = 0;
                    for (var i = 0; i < 16; i++)
                    {
                        s += leftCol[i];
                    }

                    dc = (s + 8) >> 4;
                }
                else
                {
                    dc = 128;
                }

                dst256.Fill((byte)Math.Clamp(dc, 0, 255));
                return;
            }

            case 3:
            {
                Debug.Assert(topAvail && leftAvail && topLeftAvail);

                Span<int> p = stackalloc int[33];
                p[0] = topLeftAvail ? topLeft : 0;
                for (var i = 0; i < 16; i++)
                {
                    p[1 + i] = topRow[i];
                }

                for (var i = 0; i < 16; i++)
                {
                    p[17 + i] = leftCol[i];
                }

                var hSum = 0;
                for (var i = 0; i < 8; i++)
                {
                    hSum += (i + 1) * (p[1 + 8 + i] - p[1 + 6 - i]);
                }

                var vSum = 0;
                for (var j = 0; j < 8; j++)
                {
                    // §8.3.3.4: V uses p[−1, 6−y']; for y'=7 that is p[−1,−1] = topLeft (p[0]).
                    // The left column lives at p[17..32], so 6−j only stays in-range for j≤6;
                    // j=7 must read the corner, not p[16] (which is topRow[15]).
                    var lower = j < 7 ? p[17 + 6 - j] : p[0];
                    vSum += (j + 1) * (p[17 + 8 + j] - lower);
                }

                var b = (5 * hSum + 32) >> 6;
                var c = (5 * vSum + 32) >> 6;
                var a = 16 * (p[17 + 15] + p[1 + 15]);
                for (var y = 0; y < 16; y++)
                {
                    for (var x = 0; x < 16; x++)
                    {
                        var pred = (a + b * (x - 7) + c * (y - 7) + 16) >> 5;
                        dst256[y * 16 + x] = (byte)Math.Clamp(pred, 0, 255);
                    }
                }

                return;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    /// <summary>
    /// Scores all available Intra_16×16 modes against <paramref name="src256"/> using SAD and returns
    /// the best (mode, SAD) pair. Unavailable modes (e.g. Vertical when top is missing) are skipped.
    /// </summary>
    /// <returns>(bestMode, bestSad) where bestMode is 0..3 and bestSad is the minimum SAD.</returns>
    public static (int bestMode, int bestSad) BestI16x16Mode(
        ReadOnlySpan<byte> src256,
        ReadOnlySpan<byte> topRow, bool topAvail,
        ReadOnlySpan<byte> leftCol, bool leftAvail,
        byte topLeft, bool topLeftAvail) =>
        BestI16x16Mode(src256, topRow, topAvail, leftCol, leftAvail, topLeft, topLeftAvail, H264KernelSet.CreateBest());

    public static (int bestMode, int bestSad) BestI16x16Mode(
        ReadOnlySpan<byte> src256,
        ReadOnlySpan<byte> topRow, bool topAvail,
        ReadOnlySpan<byte> leftCol, bool leftAvail,
        byte topLeft, bool topLeftAvail,
        IH264KernelSet kernels)
    {
        Span<byte> pred = stackalloc byte[256];
        var bestMode = 2;
        var bestSad = int.MaxValue;

        ReadOnlySpan<int> modeOrder = [2, 0, 1, 3];
        foreach (var m in modeOrder)
        {
            if (!IsModeAvailable(m, topAvail, leftAvail, topLeftAvail)) continue;
            kernels.PredictIntra16x16(m, topRow, topAvail, leftCol, leftAvail, topLeft, topLeftAvail, pred);
            var sad = kernels.SadIntra16x16(src256, pred, srcStride: 16);
            if (sad < bestSad) { bestSad = sad; bestMode = m; }
        }

        return (bestMode, bestSad);
    }

    /// <summary>Returns whether the given Intra_16×16 mode is valid given neighbour availability.</summary>
    public static bool IsModeAvailable(int mode, bool topAvail, bool leftAvail, bool topLeftAvail) =>
        mode switch
        {
            0 => topAvail,
            1 => leftAvail,
            2 => true,  // DC can always compute a fallback (128 when no neighbours)
            3 => topAvail && leftAvail && topLeftAvail,
            _ => false,
        };

    private static int ComputeSad256Scalar(ReadOnlySpan<byte> src, ReadOnlySpan<byte> pred)
    {
        var sad = 0;
        for (var i = 0; i < 256; i++) sad += Math.Abs(src[i] - pred[i]);
        return sad;
    }
}
