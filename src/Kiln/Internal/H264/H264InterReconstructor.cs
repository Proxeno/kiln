namespace Kiln.Internal.H264;

/// <summary>
/// Reconstructs inter-predicted luma and chroma blocks from a padded reference picture and a
/// quarter-pel motion vector, by translating MV+position into the (originX, originY, xFrac, yFrac)
/// arguments expected by the underlying sub-pel interpolators per H.264 8.4.1.
/// </summary>
/// <remarks>
/// Integer MV components use arithmetic shift (<c>&gt;&gt;</c>) so negative quarter-pel values floor
/// toward −∞ as required by H.264 8.4.2. Wrapping here avoids drift vs decoder reconstruction when
/// the slice encoder passes signed MVs.
/// </remarks>
internal static class H264InterReconstructor
{
    /// <summary>Halo used around the luma reference picture (6-tap qpel margin).</summary>
    internal const int DefaultRefHaloLuma = 16;

    /// <summary>Halo used around each chroma reference plane (bilinear + fractional pel). Keep in sync with <see cref="H264FrameSharedState.HaloChroma"/>.</summary>
    internal const int DefaultRefHaloChroma = H264FrameSharedState.HaloChroma;

    /// <summary>
    /// Same as <see cref="IsMvSafeForInterBlockAtMb"/> for a full 16×16 luma macroblock (8×8 chroma).
    /// Median / skip MVs can be valid for neighbour MBs but out of range for the current MB.
    /// </summary>
    internal static bool IsMvSafeForInter16x16AtMb(
        int pictureWidth,
        int pictureHeight,
        int mbX,
        int mbY,
        int mvX_qpel,
        int mvY_qpel,
        int haloLuma = DefaultRefHaloLuma,
        int haloChroma = DefaultRefHaloChroma) =>
        IsMvSafeForInterBlockAtMb(
            pictureWidth, pictureHeight,
            mbX, mbY,
            16, 16,
            mvX_qpel, mvY_qpel,
            haloLuma, haloChroma);

    /// <summary>
    /// True if inter prediction for a luma block of size <paramref name="lumaBlockW"/> × <paramref name="lumaBlockH"/>
    /// at unpadded top-left (<paramref name="blockUnpaddedLumaX"/>, <paramref name="blockUnpaddedLumaY"/>) with
    /// quarter-pel MV stays inside padded luma + chroma references for 4:2:0.
    /// </summary>
    internal static bool IsMvSafeForInterBlockAtMb(
        int pictureWidth,
        int pictureHeight,
        int blockUnpaddedLumaX,
        int blockUnpaddedLumaY,
        int lumaBlockW,
        int lumaBlockH,
        int mvX_qpel,
        int mvY_qpel,
        int haloLuma = DefaultRefHaloLuma,
        int haloChroma = DefaultRefHaloChroma)
    {
        var paddedStrideY = pictureWidth + 2 * haloLuma;
        var paddedHeightY = pictureHeight + 2 * haloLuma;
        var uvW = pictureWidth / 2;
        var uvH = pictureHeight / 2;
        var paddedStrideUv = uvW + 2 * haloChroma;
        var paddedHeightUv = uvH + 2 * haloChroma;

        var mvIntLx = mvX_qpel >> 2;
        var mvIntLy = mvY_qpel >> 2;
        var oxL = blockUnpaddedLumaX + mvIntLx + haloLuma;
        var oyL = blockUnpaddedLumaY + mvIntLy + haloLuma;
        if (oxL < 2 || oyL < 2)
            return false;
        if (oxL + lumaBlockW + 3 > paddedStrideY || oyL + lumaBlockH + 3 > paddedHeightY)
            return false;

        var mbCx = blockUnpaddedLumaX / 2;
        var mbCy = blockUnpaddedLumaY / 2;
        var mvIntCx = mvX_qpel >> 3;
        var mvIntCy = mvY_qpel >> 3;
        var xc = mvX_qpel & 7;
        var yc = mvY_qpel & 7;
        var oxC = mbCx + mvIntCx + haloChroma;
        var oyC = mbCy + mvIntCy + haloChroma;

        if (oxC < 0 || oyC < 0)
            return false;

        var cBw = lumaBlockW / 2;
        var cBh = lumaBlockH / 2;
        if (xc == 0 && yc == 0)
        {
            if (oxC + cBw > paddedStrideUv || oyC + cBh > paddedHeightUv)
                return false;
        }
        else
        {
            if (oxC + cBw >= paddedStrideUv || oyC + cBh >= paddedHeightUv)
                return false;
        }

        return true;
    }

    /// <summary>Reconstruct one luma block of size <paramref name="blockWidth"/> × <paramref name="blockHeight"/> at MB position (<paramref name="mbX"/>, <paramref name="mbY"/>) with quarter-pel MV (<paramref name="mvX_qpel"/>, <paramref name="mvY_qpel"/>).</summary>
    /// <param name="paddedReference">Padded luma reference plane (output of <see cref="H264ReferencePicturePadder.Pad"/> with halo ≥ 16).</param>
    /// <param name="paddedRefStride">Row stride of <paramref name="paddedReference"/>.</param>
    /// <param name="haloLuma">Halo size used when producing <paramref name="paddedReference"/>.</param>
    /// <param name="mbX">Macroblock top-left x in the (un-padded) picture.</param>
    /// <param name="mbY">Macroblock top-left y in the (un-padded) picture.</param>
    /// <param name="mvX_qpel">Luma MV x in quarter-pel units (signed; integer part = mvX_qpel / 4 rounded toward -∞).</param>
    /// <param name="mvY_qpel">Luma MV y in quarter-pel units.</param>
    /// <param name="blockWidth">Block width (4, 8, or 16).</param>
    /// <param name="blockHeight">Block height (4, 8, or 16).</param>
    /// <param name="dst">Destination buffer of size blockWidth · blockHeight, row-major.</param>
    /// <param name="dstStride">Row stride of <paramref name="dst"/>.</param>
    public static void ReconstructLuma(
        ReadOnlySpan<byte> paddedReference, int paddedRefStride, int haloLuma,
        int mbX, int mbY,
        int mvX_qpel, int mvY_qpel,
        int blockWidth, int blockHeight,
        Span<byte> dst, int dstStride)
    {
        // H.264 8.4.2: signed MV decomposition — >> 2 floors negatives; & 3 recovers 0..3 fractional.
        var mvIntX = mvX_qpel >> 2;
        var mvIntY = mvY_qpel >> 2;
        var xFrac = mvX_qpel & 3;
        var yFrac = mvY_qpel & 3;

        var srcOriginX = mbX + mvIntX + haloLuma;
        var srcOriginY = mbY + mvIntY + haloLuma;

        H264QpelLumaInterp.Interpolate(
            paddedReference, paddedRefStride,
            srcOriginX, srcOriginY,
            xFrac, yFrac,
            blockWidth, blockHeight,
            dst, dstStride);
    }

    public static void ReconstructLuma(
        ReadOnlySpan<byte> paddedReference, int paddedRefStride, int haloLuma,
        int mbX, int mbY,
        int mvX_qpel, int mvY_qpel,
        int blockWidth, int blockHeight,
        Span<byte> dst, int dstStride,
        IH264KernelSet kernels)
    {
        var mvIntX = mvX_qpel >> 2;
        var mvIntY = mvY_qpel >> 2;
        var xFrac = mvX_qpel & 3;
        var yFrac = mvY_qpel & 3;
        var srcOriginX = mbX + mvIntX + haloLuma;
        var srcOriginY = mbY + mvIntY + haloLuma;
        kernels.InterpolateLuma(
            paddedReference, paddedRefStride,
            srcOriginX, srcOriginY,
            xFrac, yFrac,
            blockWidth, blockHeight,
            dst, dstStride);
    }

    /// <summary>Reconstruct one chroma block (Cb or Cr) using <see cref="H264BilinearChromaInterp"/> with MV scaled per H.264 8.4.1.4 (chroma MV = luma MV / 2 in 1/8-pel units).</summary>
    /// <param name="paddedReference">Padded chroma reference plane (output of <see cref="H264ReferencePicturePadder.Pad"/> with halo ≥ 8).</param>
    /// <param name="paddedRefStride">Row stride.</param>
    /// <param name="haloChroma">Halo size used when producing the padded chroma plane.</param>
    /// <param name="mbCx">Chroma MB top-left x in the (un-padded) chroma picture (= mbX / 2 for 4:2:0).</param>
    /// <param name="mbCy">Chroma MB top-left y in the (un-padded) chroma picture (= mbY / 2 for 4:2:0).</param>
    /// <param name="lumaMvX_qpel">LUMA MV x in quarter-pel units. The function scales internally to chroma 1/8-pel.</param>
    /// <param name="lumaMvY_qpel">LUMA MV y in quarter-pel units.</param>
    /// <param name="blockWidth">Chroma block width (4 or 8).</param>
    /// <param name="blockHeight">Chroma block height (4 or 8).</param>
    /// <param name="dst">Destination buffer.</param>
    /// <param name="dstStride">Row stride.</param>
    public static void ReconstructChroma(
        ReadOnlySpan<byte> paddedReference, int paddedRefStride, int haloChroma,
        int mbCx, int mbCy,
        int lumaMvX_qpel, int lumaMvY_qpel,
        int blockWidth, int blockHeight,
        Span<byte> dst, int dstStride)
    {
        // H.264 8.4.1.4 (4:2:0): chroma integer pel = luma qpel >> 3; 1/8-pel frac = luma qpel & 7.
        // Arithmetic shift matches spec floor for negative MVs — do not replace with / 8.
        var mvIntX = lumaMvX_qpel >> 3;
        var mvIntY = lumaMvY_qpel >> 3;
        var xFrac = lumaMvX_qpel & 7;
        var yFrac = lumaMvY_qpel & 7;

        var srcOriginX = mbCx + mvIntX + haloChroma;
        var srcOriginY = mbCy + mvIntY + haloChroma;

        H264BilinearChromaInterp.Interpolate(
            paddedReference, paddedRefStride,
            srcOriginX, srcOriginY,
            xFrac, yFrac,
            blockWidth, blockHeight,
            dst, dstStride);
    }

    public static void ReconstructChroma(
        ReadOnlySpan<byte> paddedReference, int paddedRefStride, int haloChroma,
        int mbCx, int mbCy,
        int lumaMvX_qpel, int lumaMvY_qpel,
        int blockWidth, int blockHeight,
        Span<byte> dst, int dstStride,
        IH264KernelSet kernels)
    {
        var mvIntX = lumaMvX_qpel >> 3;
        var mvIntY = lumaMvY_qpel >> 3;
        var xFrac = lumaMvX_qpel & 7;
        var yFrac = lumaMvY_qpel & 7;
        var srcOriginX = mbCx + mvIntX + haloChroma;
        var srcOriginY = mbCy + mvIntY + haloChroma;
        kernels.InterpolateChroma(
            paddedReference, paddedRefStride,
            srcOriginX, srcOriginY,
            xFrac, yFrac,
            blockWidth, blockHeight,
            dst, dstStride);
    }
}
