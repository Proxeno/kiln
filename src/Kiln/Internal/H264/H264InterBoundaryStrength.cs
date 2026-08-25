namespace Kiln.Internal.H264;

/// <summary>
/// Per-edge boundary-strength (bs) for inter macroblocks per H.264 8.7.2.1. Intra MBs use bs=3 (internal
/// edges) or bs=4 (macroblock edges) and are computed elsewhere in the slice encoder; this function fills
/// the inter case (<c>bs</c> in { 0, 1, 2 }) before the slice encoder overlays intra/neighbour-specific <c>3</c>/<c>4</c>.
/// </summary>
internal static class H264InterBoundaryStrength
{
    /// <summary>One 4×4-block sample from the encoder's MV+ref grid for boundary-strength comparison.</summary>
    /// <param name="RefIdx">Reference index of the 4×4 block (-1 if intra; treat as "different ref" for bs purposes).</param>
    /// <param name="MvXQpel">Luma MV x in quarter-pel units.</param>
    /// <param name="MvYQpel">Luma MV y in quarter-pel units.</param>
    /// <param name="NonZeroCoeffs">True when this 4×4 had coded non-zero coefficients in the coded bitstream (luma).</param>
    public readonly record struct InterEdgeNeighbour(int RefIdx, int MvXQpel, int MvYQpel, bool NonZeroCoeffs = false);

    /// <summary>
    /// Fill <paramref name="bsHorizontal"/> (length 16) and <paramref name="bsVertical"/> (length 16) for one
    /// macroblock whose 16 4×4 sub-blocks have the MV/ref state in <paramref name="thisMbBlocks"/>, with the
    /// MB above (4 bottom 4×4 blocks) in <paramref name="aboveMbBottomRow"/> and the MB to the left
    /// (4 right-column 4×4 blocks) in <paramref name="leftMbRightCol"/>. Use an empty span for either neighbour
    /// at the picture boundary; the function fills bs=0 for the corresponding edge segments.
    /// </summary>
    public static void Compute(
        ReadOnlySpan<InterEdgeNeighbour> thisMbBlocks,        // length 16, raster order (b0=top-left, b15=bot-right)
        ReadOnlySpan<InterEdgeNeighbour> aboveMbBottomRow,    // length 4 or empty, left→right
        ReadOnlySpan<InterEdgeNeighbour> leftMbRightCol,      // length 4 or empty, top→bottom
        Span<byte> bsHorizontal,                               // length 16, edge order matches the slice encoder's _bsHorizontal layout
        Span<byte> bsVertical)                                // length 16
    {
        // External top edge (edge=0 of bsHorizontal): row 0 of this MB vs aboveMbBottomRow
        for (var seg = 0; seg < 4; seg++)
        {
            if (aboveMbBottomRow.IsEmpty)
            {
                bsHorizontal[seg] = 0;
                continue;
            }

            bsHorizontal[seg] = ComputeBs(aboveMbBottomRow[seg], thisMbBlocks[seg]);
        }

        // Internal horizontal edges (edge=1, 2, 3): between row r-1 and row r
        for (var edge = 1; edge < 4; edge++)
        {
            for (var seg = 0; seg < 4; seg++)
            {
                var blkAbove = thisMbBlocks[(edge - 1) * 4 + seg];
                var blkBelow = thisMbBlocks[edge * 4 + seg];
                bsHorizontal[edge * 4 + seg] = ComputeBs(blkAbove, blkBelow);
            }
        }

        // External left edge (edge=0 of bsVertical): col 0 of this MB vs leftMbRightCol
        for (var seg = 0; seg < 4; seg++)
        {
            if (leftMbRightCol.IsEmpty)
            {
                bsVertical[seg] = 0;
                continue;
            }

            bsVertical[seg] = ComputeBs(leftMbRightCol[seg], thisMbBlocks[seg * 4 + 0]);
        }

        // Internal vertical edges (edge=1, 2, 3): between col c-1 and col c
        for (var edge = 1; edge < 4; edge++)
        {
            for (var seg = 0; seg < 4; seg++)
            {
                var blkLeft = thisMbBlocks[seg * 4 + (edge - 1)];
                var blkRight = thisMbBlocks[seg * 4 + edge];
                bsVertical[edge * 4 + seg] = ComputeBs(blkLeft, blkRight);
            }
        }
    }

    /// <summary>
    /// H.264 8.7.2.1 (inter–inter, after the intra branches in the slice encoder), in the clause's
    /// order of precedence: either adjacent 4×4 luma block has non-zero transform coefficient levels
    /// → <c>2</c>; else reference index mismatch or <c>|Δmvx| ≥ 4</c> / <c>|Δmvy| ≥ 4</c> qpel →
    /// <c>1</c>; else <c>0</c>. When <c>RefIdx</c> mismatches (<c>-1</c> intra vs inter) the ≥ 1
    /// result lets the slice encoder overlay <c>3</c>/<c>4</c> on true intra edges.
    /// <para>
    /// The coefficient condition must be tested <em>before</em> the MV/ref condition: a historical
    /// version returned 1 on MV difference first, which mis-derived every coefficient-carrying edge
    /// between differently-moving blocks as bS=1. Table 8-17 masks the difference wherever
    /// tC0[indexA, 0] == tC0[indexA, 1] — true at the QPs the byte-exact oracle tests happened to
    /// use (23, 28, 33, 34) — but at e.g. QP 31/32/35/36 (or any per-MB-QP stream whose edge
    /// averages land there) the encoder's reconstruction silently drifted from every conformant
    /// decoder's, compounding through the DPB exactly like the v0.2.0 P_Skip bug.
    /// </para>
    /// </summary>
    private static byte ComputeBs(InterEdgeNeighbour a, InterEdgeNeighbour b)
    {
        if (a.NonZeroCoeffs || b.NonZeroCoeffs)
        {
            return 2;
        }

        if (a.RefIdx != b.RefIdx)
        {
            return 1;
        }

        if (Math.Abs(a.MvXQpel - b.MvXQpel) >= 4)
        {
            return 1;
        }

        if (Math.Abs(a.MvYQpel - b.MvYQpel) >= 4)
        {
            return 1;
        }

        return 0;
    }
}
