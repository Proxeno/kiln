namespace Kiln.Internal.H264;

/// <summary>
/// CAVLC writers for Intra macroblock syntax shared across I-slices and P-slices.
/// Contains the Intra_16×16 header (mb_type) and the full Intra_16×16 macroblock encoding
/// per H.264 7.3.5 (macroblock_layer) + Table 7-11/7-14.
/// </summary>
internal static class H264SliceMbWriter
{
    /// <summary>
    /// Writes the <c>mb_type</c> ue(v) for an Intra_16×16 macroblock per H.264 Tables 7-11 (I-slice)
    /// and 7-14 (P-slice). The CBP is fully encoded inside the mb_type value; no separate <c>coded_block_pattern</c>
    /// follows for Intra_16×16 MBs.
    /// </summary>
    /// <param name="bs">Destination RBSP bit buffer.</param>
    /// <param name="predMode">Intra_16×16 prediction mode: 0=Vertical, 1=Horizontal, 2=DC, 3=Plane.</param>
    /// <param name="cbpLuma">1 if any luma AC 4×4 block has nonzero coefficients, 0 otherwise.</param>
    /// <param name="cbpChroma">Chroma CBP: 0=no residual, 1=DC only, 2=DC+AC.</param>
    /// <param name="isPSlice">True when encoding inside a P-slice (adds +5 offset to the I-slice code).</param>
    public static void WriteIntra16x16Header(
        H264RbspBitBuffer bs,
        int predMode,
        int cbpLuma,
        int cbpChroma,
        bool isPSlice)
    {
        // I-slice Table 7-11: mb_type = 1 + predMode + 4·CodedBlockPatternChroma + 12·cbpLumaBit.
        // Table 7-11 column order is (predMode, cbpChroma, cbpLuma); cbpChroma ∈ {0,1,2}, cbpLumaBit ∈ {0,1}.
        // P-slice Table 7-14 adds 5 (offset for the five P-inter types that precede I types).
        var mbTypeCodeNum = 1 + predMode + 4 * cbpChroma + 12 * cbpLuma + (isPSlice ? 5 : 0);
        bs.WriteUe((uint)mbTypeCodeNum);
    }

    /// <summary>
    /// Encodes a complete Intra_16×16 macroblock layer for a P-slice per H.264 7.3.5.
    /// Emits: mb_type ue, intra_chroma_pred_mode ue, mb_qp_delta se, then the luma DC CAVLC plane,
    /// optionally 16 luma AC 4×4 residual blocks, and the chroma DC/AC residuals.
    /// </summary>
    /// <param name="bs">Destination bit buffer.</param>
    /// <param name="predMode">Luma prediction mode (0=V, 1=H, 2=DC, 3=Plane).</param>
    /// <param name="chromaPredMode">Chroma prediction mode (0=DC, 1=H, 2=V, 3=Plane).</param>
    /// <param name="chromaDcU">4 quantised Hadamard-domain chroma DC levels for Cb.</param>
    /// <param name="chromaDcV">4 quantised Hadamard-domain chroma DC levels for Cr.</param>
    /// <param name="chromaAcU">64 quantised AC levels for the 4 Cb 4×4 blocks (16 per block).</param>
    /// <param name="chromaAcV">64 quantised AC levels for the 4 Cr 4×4 blocks (16 per block).</param>
    /// <param name="lumaDcQuantised">16 quantised Hadamard-domain luma DC levels (output of H264LumaDcHadamard.QuantLumaDcHadamard).</param>
    /// <param name="lumaAcBlocks">256 quantised AC luma residuals (16 per 4×4 block, coeff[0] must be 0; null or empty if cbpLuma=0).</param>
    /// <param name="lumaBlkNonZeros">16 non-zero counts for the luma AC blocks (used for CAVLC nC prediction).</param>
    /// <param name="cbpLuma">1 if any luma AC block has nonzero coefficients.</param>
    /// <param name="cbpChroma">0=no chroma, 1=DC only, 2=DC+AC.</param>
    /// <param name="qpDelta">QP delta from the preceding coded MB (<c>qpThisMb - lastMbQp</c>).</param>
    /// <param name="isPSlice">True for P-slice context (mb_type offset).</param>
    /// <param name="ncLookup">
    ///   Function returning the CAVLC nC predictor for a given luma 4×4 raster-scan index (0..15).
    ///   Delegates to the caller's neighbour-cache logic; pass <c>null</c> to use nC=0.
    /// </param>
    /// <param name="ncLookupChroma">
    ///   Function returning the CAVLC nC predictor (H.264 9.2.1) for a chroma AC block addressed as
    ///   <c>4 × component + blockIdx</c>: 0..3 are the Cb 4×4 blocks and 4..7 the Cr 4×4 blocks, each in
    ///   raster order within the component's 8×8 area (0 = top-left, 1 = top-right, 2 = bottom-left,
    ///   3 = bottom-right), i.e. the order in which they are transmitted.
    /// </param>
    public static void WriteIntra16x16Macroblock(
        H264RbspBitBuffer bs,
        int predMode,
        int chromaPredMode,
        System.Span<short> lumaDcQuantised,
        System.Span<short> lumaAcBlocks,
        System.ReadOnlySpan<byte> lumaBlkNonZeros,
        System.Span<short> chromaDcU,
        System.Span<short> chromaDcV,
        System.Span<short> chromaAcU,
        System.Span<short> chromaAcV,
        System.ReadOnlySpan<byte> chromaNzU,
        System.ReadOnlySpan<byte> chromaNzV,
        int cbpLuma,
        int cbpChroma,
        int qpDelta,
        bool isPSlice,
        System.Func<int, int> ncLookupChroma,
        System.Func<int, int>? ncLookup = null)
    {
        WriteIntra16x16Header(bs, predMode, cbpLuma, cbpChroma, isPSlice);
        bs.WriteUe((uint)chromaPredMode);             // intra_chroma_pred_mode ue(v)
        bs.WriteSe(qpDelta);                          // mb_qp_delta se(v)

        // Luma DC residual: 16 Hadamard-domain coefficients, CAVLC with the same nc as block 0 (raster TL).
        // H.264 9.2.1: the Intra_16×16 luma DC plane uses the standard luma coeff_token tables,
        // with nC derived from the top-left block's neighbours — the same nC as for raster block 0.
        // H.264 7.3.5.3: the DC block is always transmitted even when all coefficients are zero.
        var lumaDcNc = ncLookup?.Invoke(0) ?? 0;
        H264CavlcResidual.WriteBlockResidual(bs, lumaDcQuantised[..16], 15, H264ResidualKind.Luma16x16Dc, lumaDcNc);

        // Luma AC residuals (cbpLuma == 1): 16 blocks of 15 AC coefficients each.
        // H.264 7.3.5.3 + 6.4.3 — luma 4×4 blocks are transmitted in inverse-luma-4x4-block-scan
        // (zig-zag) order, NOT raster order. <paramref name="lumaAcBlocks"/> is stored in raster
        // order by the caller, so we map each scan index → raster slot. The scan→raster table
        // mirrors <c>H264BaselineSliceEncoder.ScanIdxToBr/Bc</c>.
        if (cbpLuma == 1)
        {
            ReadOnlySpan<byte> scanToRaster = [0, 1, 4, 5, 2, 3, 6, 7, 8, 9, 12, 13, 10, 11, 14, 15];
            Span<short> ac15 = stackalloc short[15];
            for (var sIdx = 0; sIdx < 16; sIdx++)
            {
                var raster = scanToRaster[sIdx];
                for (var t = 0; t < 15; t++) ac15[t] = lumaAcBlocks[raster * 16 + 1 + t];
                var nc = ncLookup?.Invoke(raster) ?? 0;
                H264CavlcResidual.WriteBlockResidual(bs, ac15, 14, H264ResidualKind.Luma4X4, nc);
            }
        }

        // Chroma DC (cbpChroma >= 1).
        if (cbpChroma >= 1)
        {
            H264CavlcResidual.WriteBlockResidual(bs, chromaDcU, 3, H264ResidualKind.ChromaDc, 0);
            H264CavlcResidual.WriteBlockResidual(bs, chromaDcV, 3, H264ResidualKind.ChromaDc, 0);
        }

        // Chroma AC (cbpChroma == 2).
        if (cbpChroma == 2)
        {
            Span<short> acC = stackalloc short[15];
            for (var comp = 0; comp < 2; comp++)
            {
                var src = comp == 0 ? chromaAcU : chromaAcV;
                var nz = comp == 0 ? chromaNzU : chromaNzV;
                for (var cb = 0; cb < 4; cb++)
                {
                    for (var t = 0; t < 15; t++) acC[t] = src[cb * 16 + 1 + t];
                    // H.264 9.2.1: nC for a chroma AC block uses its left (A) and above (B) neighbours,
                    // which cross the macroblock boundary for the top row / left column of the 8×8 area.
                    var n = comp * 4 + cb;
                    var ncC = ncLookupChroma(n);
                    H264CavlcResidual.WriteBlockResidual(bs, acC, 14, H264ResidualKind.ChromaAc, ncC);
                }
            }
        }
    }
}
