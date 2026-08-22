namespace Kiln.Internal.H264;

/// <summary>
/// Reference-picture border replication per H.264 8.4.2.1 — produces a halo around the reconstructed
/// picture so motion estimation and sub-pel filters can read past the picture boundary without bounds
/// checks. Pure, allocation-free.
/// </summary>
internal static class H264ReferencePicturePadder
{
    /// <summary>Pad <paramref name="src"/> into <paramref name="dst"/> with a halo of replicated border samples.</summary>
    /// <param name="src">Source plane of size <paramref name="srcWidth"/> × <paramref name="srcHeight"/>, row-major.</param>
    /// <param name="srcStride">Row stride of <paramref name="src"/> (≥ <paramref name="srcWidth"/>).</param>
    /// <param name="srcWidth">Source width in samples.</param>
    /// <param name="srcHeight">Source height in samples.</param>
    /// <param name="halo">Halo width on every side; total padded plane is (srcWidth + 2·halo) × (srcHeight + 2·halo).</param>
    /// <param name="dst">Destination buffer of size (srcWidth + 2·halo) × (srcHeight + 2·halo), row-major.</param>
    /// <param name="dstStride">Row stride of <paramref name="dst"/> (≥ srcWidth + 2·halo).</param>
    public static void Pad(
        ReadOnlySpan<byte> src, int srcStride, int srcWidth, int srcHeight,
        int halo,
        Span<byte> dst, int dstStride)
    {
        var dstWidth = srcWidth + 2 * halo;

        // 1. Interior copy — aligns with oracle for x,y inside the original picture when offsets are halo.
        for (var y = 0; y < srcHeight; y++)
        {
            var rowOff = y * srcStride;
            var dstRow = (halo + y) * dstStride + halo;
            src.Slice(rowOff, srcWidth).CopyTo(dst.Slice(dstRow, srcWidth));
        }

        // 2. Horizontal border replication; corners follow once vertical bands copy these rows (prompt Inline 2).
        for (var y = 0; y < srcHeight; y++)
        {
            var rowOff = y * srcStride;
            var dstRowBase = (halo + y) * dstStride;
            var left = src[rowOff];
            var right = src[rowOff + srcWidth - 1];
            for (var x = 0; x < halo; x++)
            {
                dst[dstRowBase + x] = left;
                dst[dstRowBase + halo + srcWidth + x] = right;
            }
        }

        var firstPaddedRow = halo * dstStride;
        var lastPaddedRow = (halo + srcHeight - 1) * dstStride;

        // 3–4. Vertical halo duplicates fully padded horizontal rows — matches 8.4.2.1 clamp-to-edge semantics.
        for (var y = 0; y < halo; y++)
        {
            dst.Slice(firstPaddedRow, dstWidth).CopyTo(dst.Slice(y * dstStride, dstWidth));
            dst.Slice(lastPaddedRow, dstWidth).CopyTo(dst.Slice((halo + srcHeight + y) * dstStride, dstWidth));
        }
    }
}
