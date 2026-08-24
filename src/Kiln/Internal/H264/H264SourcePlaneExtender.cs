namespace Kiln.Internal.H264;

/// <summary>
/// Extends a caller-supplied source plane from display size up to the coded (macroblock-aligned)
/// size by replicating the last real column and row. The extension samples are coded, deblocked,
/// and stored in the DPB like any other samples, so they must be plausible picture content:
/// zero-fill is never safe — when the crop boundary falls inside a 4×4 block, the §8.7 in-loop
/// filter reads the padding into its activity test and updates <em>visible</em> samples across
/// the crop edge. Replication also lets the trailing macroblocks collapse to CBP=0 / P_Skip
/// instead of coding a full high-frequency residual for invisible content.
/// </summary>
/// <remarks>
/// This is <em>not</em> <see cref="H264ReferencePicturePadder"/>: that builds a symmetric halo
/// around a reconstructed reference picture per §8.4.2.1 so ME/qpel can read past the picture
/// boundary. This one runs before encoding, on the source, right/bottom only, and is unrelated
/// to any spec clause — the coded picture simply must cover the full MB grid (§7.4.2.1.1).
/// </remarks>
internal static class H264SourcePlaneExtender
{
    /// <summary>
    /// Copy <paramref name="src"/> (display-sized) into <paramref name="dst"/> (coded-sized),
    /// replicating the last real column into columns [<paramref name="srcWidth"/>, <paramref name="dstWidth"/>)
    /// and then the last real row into rows [<paramref name="srcHeight"/>, <paramref name="dstHeight"/>)
    /// — so the bottom-right corner region repeats the bottom-right source sample.
    /// </summary>
    /// <param name="src">Source plane, row-major, <paramref name="srcHeight"/> rows of <paramref name="srcWidth"/> samples.</param>
    /// <param name="srcStride">Row stride of <paramref name="src"/> (≥ <paramref name="srcWidth"/>).</param>
    /// <param name="srcWidth">Display width in samples.</param>
    /// <param name="srcHeight">Display height in samples.</param>
    /// <param name="dst">Destination plane, row-major, sized <paramref name="dstWidth"/> × <paramref name="dstHeight"/>.</param>
    /// <param name="dstStride">Row stride of <paramref name="dst"/> (≥ <paramref name="dstWidth"/>).</param>
    /// <param name="dstWidth">Coded width in samples (≥ <paramref name="srcWidth"/>).</param>
    /// <param name="dstHeight">Coded height in samples (≥ <paramref name="srcHeight"/>).</param>
    public static void Extend(
        ReadOnlySpan<byte> src, int srcStride, int srcWidth, int srcHeight,
        Span<byte> dst, int dstStride, int dstWidth, int dstHeight)
    {
        // 1. Row copies plus right-edge replication of the last real column.
        for (var y = 0; y < srcHeight; y++)
        {
            var dstRow = y * dstStride;
            src.Slice(y * srcStride, srcWidth).CopyTo(dst.Slice(dstRow, srcWidth));
            var right = dst[dstRow + srcWidth - 1];
            for (var x = srcWidth; x < dstWidth; x++)
            {
                dst[dstRow + x] = right;
            }
        }

        // 2. Bottom-edge replication of the last fully-extended row (covers the corner too).
        var lastRealRow = (srcHeight - 1) * dstStride;
        for (var y = srcHeight; y < dstHeight; y++)
        {
            dst.Slice(lastRealRow, dstWidth).CopyTo(dst.Slice(y * dstStride, dstWidth));
        }
    }
}
