namespace Kiln.Tests;

/// <summary>
/// P-slice syntax tracer: walks a P-slice RBSP from a known header-end bit offset and records
/// per-MB bit consumption, MB type, and motion/CBP summary.
/// Used to diff against the encoder-side bit log (see <see cref="H264PSliceLocateDivergenceTests"/>)
/// and locate the first bit-count divergence.
///
/// Supports: P_Skip, P_L0_16x16, P_L0_L0_16x8, P_L0_L0_8x16, P_8x8(all sub_mb_types),
///           P_8x8ref0, I_16x16-in-P (all subtypes), I_4x4-in-P.
/// Assumes numRefIdxActiveMinus1=0 (single reference).
/// </summary>
internal static class H264PSliceTraceDecoder
{
    public sealed record MbTrace(
        int MbIdx,
        int AbsBitsBefore,   // absolute bit offset before this MB's first written element
        int AbsBitsAfter,    // absolute bit offset after all this MB's bits
        string MbTypeDesc,
        byte[]? LumaNzAfter = null,   // lumaNz snapshot after this MB (optional, for nc diagnostics)
        byte[]? ChromaNzAfter = null);

    public static int BitsConsumed(this MbTrace t) => t.AbsBitsAfter - t.AbsBitsBefore;

    /// <summary>
    /// Traces the P-slice data in <paramref name="rbsp"/> starting at bit offset
    /// <paramref name="headerBits"/> (i.e. the bit after the last slice-header bit).
    /// Returns one entry per MB including skip-run MBs.
    /// When <paramref name="forceNcZero"/> is true every CAVLC block is decoded with nc=0
    /// (useful to test whether nc computation is the source of a bitstream mismatch).
    /// <paramref name="numRefIdxActiveMinus1"/> must match the slice header value (0 for
    /// single-reference streams; 1 when two references are active in the DPB).
    /// Optional <paramref name="log"/> sink collects "<mb>:<bit>:<msg>" entries.
    /// </summary>
    public static List<MbTrace> Trace(byte[] rbsp, int headerBits, int mbWidth, int mbHeight,
        bool forceNcZero = false, List<string>? log = null, int numRefIdxActiveMinus1 = 0)
    {
        var mbCount = mbWidth * mbHeight;
        var br = new H264CavlcSpecDecode.BitReader(rbsp);
        // Skip the header bits.
        for (var i = 0; i < headerBits; i++) br.ReadBit();

        // Non-zero counts: luma 16 per MB, chroma 8 per MB (4 Cb + 4 Cr).
        var lumaNz = new byte[mbCount * 16];
        var chromaNz = new byte[mbCount * 8];

        var result = new List<MbTrace>(mbCount);
        var mb = 0;

        while (mb < mbCount)
        {
            var beforeSkipRun = br.BitPosition;
            var skipRun = ReadUe(br);

            for (var s = 0; s < skipRun && mb < mbCount; s++, mb++)
            {
                // A skip MB's "bits" are the mb_skip_run prefix (shared with the first skip of the run).
                // By convention we assign all run bits to the first skip MB and 0 to the rest.
                var runBits = s == 0 ? br.BitPosition - beforeSkipRun : 0;
                var pos = s == 0 ? br.BitPosition : br.BitPosition;
                result.Add(new MbTrace(mb, beforeSkipRun, pos, "P_Skip"));
            }

            if (mb >= mbCount) break;

            var mbStart = br.BitPosition;
            var mbTypeRaw = (int)ReadUe(br);
            string desc;

            log?.Add($"MB{mb}@bit{mbStart}: mb_type={mbTypeRaw}");
            if (mbTypeRaw <= 4)
            {
                // P-inter MB.
                switch (mbTypeRaw)
                {
                    case 0:
                    {
                        ReadTe(br, numRefIdxActiveMinus1);
                        var mvdX = ReadSe(br);
                        var mvdY = ReadSe(br);
                        desc = $"P_16x16 mvd=({mvdX},{mvdY})";
                        break;
                    }
                    case 1:
                    {
                        desc = "P_16x8";
                        ReadTe(br, numRefIdxActiveMinus1); ReadTe(br, numRefIdxActiveMinus1);
                        var mvd0X = ReadSe(br);
                        var mvd0Y = ReadSe(br);
                        var mvd1X = ReadSe(br);
                        var mvd1Y = ReadSe(br);
                        desc = $"P_16x8 mvd0=({mvd0X},{mvd0Y}) mvd1=({mvd1X},{mvd1Y})";
                        break;
                    }
                    case 2:
                    {
                        desc = "P_8x16";
                        ReadTe(br, numRefIdxActiveMinus1); ReadTe(br, numRefIdxActiveMinus1);
                        var mvd0X = ReadSe(br);
                        var mvd0Y = ReadSe(br);
                        var mvd1X = ReadSe(br);
                        var mvd1Y = ReadSe(br);
                        desc = $"P_8x16 mvd0=({mvd0X},{mvd0Y}) mvd1=({mvd1X},{mvd1Y})";
                        break;
                    }
                    case 3:
                    {
                        var st = new int[4];
                        for (var i = 0; i < 4; i++) st[i] = (int)ReadUe(br);
                        for (var i = 0; i < 4; i++) ReadTe(br, numRefIdxActiveMinus1);
                        for (var i = 0; i < 4; i++)
                            for (var p = 0; p < SubMbParts(st[i]); p++) { ReadSe(br); ReadSe(br); }
                        desc = $"P_8x8[{st[0]}{st[1]}{st[2]}{st[3]}]";
                        break;
                    }
                    default: // 4 = P_8x8ref0
                    {
                        var st = new int[4];
                        for (var i = 0; i < 4; i++) st[i] = (int)ReadUe(br);
                        for (var i = 0; i < 4; i++)
                            for (var p = 0; p < SubMbParts(st[i]); p++) { ReadSe(br); ReadSe(br); }
                        desc = "P_8x8ref0";
                        break;
                    }
                }

                var cbp = ReadInterCbp(br);
                desc = $"{desc} cbp={cbp}";
                var cbpLuma = cbp & 0x0F;
                var cbpChroma = cbp >> 4;
                if (cbp != 0)
                {
                    var qpd = ReadSe(br); // mb_qp_delta
                    log?.Add($"MB{mb}: qp_delta={qpd}");
                }
                ReadInterLuma(br, mb, mbWidth, cbpLuma, lumaNz, forceNcZero);
                ReadChroma(br, mb, mbWidth, cbpChroma, lumaNz, chromaNz, forceNcZero);
            }
            else
            {
                // I-type embedded in P-slice.
                var iMbType = mbTypeRaw - 5;
                if (iMbType == 0)
                {
                    // I_4x4
                    for (var blk = 0; blk < 16; blk++)
                    {
                        var prevFlag = br.ReadBit();
                        if (prevFlag == 0) br.ReadBits(3);
                    }
                    ReadUe(br); // intra_chroma_pred_mode
                    var cbp = ReadIntraCbp(br);
                    var cbpLuma = cbp & 0x0F;
                    var cbpChroma = cbp >> 4;
                    if (cbp != 0)
                    {
                        var qpd = ReadSe(br); // mb_qp_delta
                        log?.Add($"MB{mb}: qp_delta={qpd}");
                    }
                    ReadIntra4x4Luma(br, mb, mbWidth, cbpLuma, lumaNz, forceNcZero);
                    ReadChroma(br, mb, mbWidth, cbpChroma, lumaNz, chromaNz, forceNcZero);
                    desc = "I_4x4";
                }
                else if (iMbType >= 1 && iMbType <= 24)
                {
                    // I_16x16
                    var raw = iMbType - 1;
                    var predMode = raw % 4;
                    var cbpChroma = (raw / 4) % 3;
                    var cbpLumaAc = raw / 12;
                    ReadUe(br); // intra_chroma_pred_mode
                    {
                        var qpd = ReadSe(br); // mb_qp_delta (always present for I_16x16)
                        log?.Add($"MB{mb}: qp_delta={qpd}");
                    }
                    ReadI16x16Luma(br, mb, mbWidth, cbpLumaAc, lumaNz, forceNcZero, log);
                    ReadChroma(br, mb, mbWidth, cbpChroma, lumaNz, chromaNz, forceNcZero);
                    desc = $"I_16x16(pm={predMode},la={cbpLumaAc},cr={cbpChroma})";
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Unsupported mb_type={mbTypeRaw} (I_mbType={iMbType}) at mbIdx={mb} bit={br.BitPosition}");
                }
            }

            result.Add(new MbTrace(mb, mbStart, br.BitPosition, desc));
            mb++;
        }

        return result;
    }

    // ── Exp-Golomb / syntax-element readers ────────────────────────────────────────────────────

    private static uint ReadUe(H264CavlcSpecDecode.BitReader br)
    {
        var zeros = 0;
        while (br.ReadBit() == 0) zeros++;
        if (zeros == 0) return 0;
        return (uint)((1 << zeros) - 1 + br.ReadBits(zeros));
    }

    private static int ReadSe(H264CavlcSpecDecode.BitReader br)
    {
        var c = (int)ReadUe(br);
        return c % 2 == 0 ? -(c / 2) : (c + 1) / 2;
    }

    private static void ReadTe(H264CavlcSpecDecode.BitReader br, int range)
    {
        if (range == 0) return;
        if (range == 1) { br.ReadBit(); return; }
        ReadUe(br);
    }

    private static int ReadInterCbp(H264CavlcSpecDecode.BitReader br)
        => GolombToInterCbp[(int)ReadUe(br)];

    private static int ReadIntraCbp(H264CavlcSpecDecode.BitReader br)
        => GolombToIntraCbp[(int)ReadUe(br)];

    // ── CAVLC residual readers ──────────────────────────────────────────────────────────────────

    private static void ReadInterLuma(
        H264CavlcSpecDecode.BitReader br, int mbIdx, int mbWidth, int cbpLuma, byte[] lumaNz,
        bool forceNcZero = false)
    {
        ReadOnlySpan<byte> scanBr = [0, 0, 1, 1, 0, 0, 1, 1, 2, 2, 3, 3, 2, 2, 3, 3];
        ReadOnlySpan<byte> scanBc = [0, 1, 0, 1, 2, 3, 2, 3, 0, 1, 0, 1, 2, 3, 2, 3];
        for (var sIdx = 0; sIdx < 16; sIdx++)
        {
            var br_ = scanBr[sIdx]; var bc_ = scanBc[sIdx];
            if ((cbpLuma & (1 << ((br_ >> 1) * 2 + (bc_ >> 1)))) == 0) continue;
            var raster = (br_ << 2) + bc_;
            var nc = forceNcZero ? 0 : ComputeNc(mbIdx, raster, br_, bc_, mbWidth, lumaNz);
            var coeffs = DecodeAndCount(br, 15, nc, false);
            lumaNz[mbIdx * 16 + raster] = (byte)CountNz(coeffs);
        }
    }

    private static void ReadIntra4x4Luma(
        H264CavlcSpecDecode.BitReader br, int mbIdx, int mbWidth, int cbpLuma, byte[] lumaNz,
        bool forceNcZero = false)
    {
        // Intra_4x4 CBP luma: bits 0..3 = 8x8 blocks; same layout as inter.
        ReadInterLuma(br, mbIdx, mbWidth, cbpLuma, lumaNz, forceNcZero);
    }

    private static void ReadI16x16Luma(
        H264CavlcSpecDecode.BitReader br, int mbIdx, int mbWidth, int cbpLumaAc, byte[] lumaNz,
        bool forceNcZero = false, List<string>? log = null)
    {
        // Luma DC block (always present, endIdx=15, nc from neighbour of raster block 0).
        var ncDc = forceNcZero ? 0 : ComputeNc(mbIdx, 0, 0, 0, mbWidth, lumaNz);
        log?.Add($"  I16Luma DC: nc={ncDc} @bit{br.BitPosition}");
        var dcCoeffs = DecodeAndCount(br, 15, ncDc, false);
        log?.Add($"  I16Luma DC: after @bit{br.BitPosition}, nz={CountNz(dcCoeffs)}");

        if (cbpLumaAc == 0) return;

        // Luma AC blocks (×16, endIdx=14).
        ReadOnlySpan<byte> scanBr = [0, 0, 1, 1, 0, 0, 1, 1, 2, 2, 3, 3, 2, 2, 3, 3];
        ReadOnlySpan<byte> scanBc = [0, 1, 0, 1, 2, 3, 2, 3, 0, 1, 0, 1, 2, 3, 2, 3];
        for (var blk = 0; blk < 16; blk++)
        {
            var br_ = scanBr[blk]; var bc_ = scanBc[blk];
            var raster = (br_ << 2) + bc_;
            var nc = forceNcZero ? 0 : ComputeNc(mbIdx, raster, br_, bc_, mbWidth, lumaNz);
            log?.Add($"  I16Luma AC[scan={blk},raster={raster}]: nc={nc} @bit{br.BitPosition}");
            var coeffs = DecodeAndCount(br, 14, nc, false);
            var nz = (byte)CountNz(coeffs);
            log?.Add($"  I16Luma AC[scan={blk},raster={raster}]: after @bit{br.BitPosition}, nz={nz}");
            lumaNz[mbIdx * 16 + raster] = nz;
        }
    }

    private static void ReadChroma(
        H264CavlcSpecDecode.BitReader br, int mbIdx, int mbWidth, int cbpChroma,
        byte[] lumaNz, byte[] chromaNz, bool forceNcZero = false)
    {
        if (cbpChroma < 1) return;

        // Chroma DC (2 blocks, isChromaDc=true, endIdx=3, nc ignored).
        DecodeAndCount(br, 3, 0, true);  // Cb DC
        DecodeAndCount(br, 3, 0, true);  // Cr DC

        if (cbpChroma < 2) return;

        // Chroma AC (4 Cb + 4 Cr, endIdx=14).
        var chromaCtx = new sbyte[ChromaCtxSlots];
        FillChromaNzcContext(mbIdx, mbWidth, chromaNz, chromaCtx);

        for (var comp = 0; comp < 2; comp++)
        {
            for (var cb = 0; cb < 4; cb++)
            {
                var slot = ChromaCtxSlot(comp, cb >> 1, cb & 1);
                var nc = forceNcZero ? 0 : DeriveNc(chromaCtx[slot - 1], chromaCtx[slot - ChromaCtxStride]);
                var coeffs = DecodeAndCount(br, 14, nc, false);
                var nz = (byte)CountNz(coeffs);
                chromaCtx[slot] = (sbyte)nz;
                chromaNz[mbIdx * 8 + comp * 4 + cb] = nz;
            }
        }
    }

    // Decode one CAVLC block and return coefficients. Wraps H264CavlcSpecDecode.DecodeBlock
    // to snapshot BitPosition before/after for bit-accounting purposes.
    private static short[] DecodeAndCount(
        H264CavlcSpecDecode.BitReader br, int endIdx, int nc, bool isChromaDc)
        => H264CavlcSpecDecode.DecodeBlock(br, endIdx, nc, isChromaDc);

    // ── nc computation (mirrors H264BaselineSliceEncoder's nC derivation) ─────────────────────
    //
    // H.264 §9.2.1: nC is the average of the total coefficient counts of neighbours A (left) and B
    // (above) when both are available, that single neighbour when only one is, and 0 when neither
    // is. Neighbours are located per §6.4.11.4; unavailable neighbours use -1.

    private const int NcUnavailable = -1;

    /// <summary>Row stride of one chroma component's neighbour-context grid (2 blocks + one halo column).</summary>
    private const int ChromaCtxStride = 3;

    private const int ChromaCtxSlotsPerComponent = ChromaCtxStride * 3;

    private const int ChromaCtxSlots = ChromaCtxSlotsPerComponent * 2;

    private static int ChromaCtxSlot(int component, int row, int col) =>
        component * ChromaCtxSlotsPerComponent + (row + 1) * ChromaCtxStride + col + 1;

    private static int ComputeNc(int mbIdx, int raster, int blockBr, int blockBc, int mbWidth, byte[] lumaNz)
    {
        var mbx = mbIdx % mbWidth;
        var mby = mbIdx / mbWidth;
        int nA = blockBc > 0 ? lumaNz[mbIdx * 16 + raster - 1]
            : mbx > 0 ? lumaNz[(mbIdx - 1) * 16 + blockBr * 4 + 3]
            : NcUnavailable;
        int nB = blockBr > 0 ? lumaNz[mbIdx * 16 + raster - 4]
            : mby > 0 ? lumaNz[(mbIdx - mbWidth) * 16 + raster + 12]
            : NcUnavailable;
        return DeriveNc(nA, nB);
    }

    private static int DeriveNc(int nA, int nB)
    {
        if (nA < 0) return nB < 0 ? 0 : nB;
        return nB < 0 ? nA : (nA + nB + 1) >> 1;
    }

    /// <summary>
    /// Seeds the top / left halo of the two-component chroma neighbour-context grid from the left and
    /// above macroblocks' edge 4×4 chroma blocks.
    /// </summary>
    private static void FillChromaNzcContext(int mbIdx, int mbWidth, byte[] chromaNz, sbyte[] ctx)
    {
        Array.Fill(ctx, (sbyte)NcUnavailable);

        if (mbIdx % mbWidth > 0)
        {
            var left = (mbIdx - 1) * 8;
            for (var comp = 0; comp < 2; comp++)
            {
                for (var row = 0; row < 2; row++)
                {
                    ctx[ChromaCtxSlot(comp, row, -1)] = (sbyte)chromaNz[left + comp * 4 + row * 2 + 1];
                }
            }
        }

        if (mbIdx / mbWidth > 0)
        {
            var above = (mbIdx - mbWidth) * 8;
            for (var comp = 0; comp < 2; comp++)
            {
                for (var col = 0; col < 2; col++)
                {
                    ctx[ChromaCtxSlot(comp, -1, col)] = (sbyte)chromaNz[above + comp * 4 + 2 + col];
                }
            }
        }
    }

    private static int CountNz(short[] c) => c.Count(x => x != 0);

    private static int SubMbParts(int sub) => sub switch
    {
        0 => 1, 1 => 2, 2 => 2, 3 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(sub)),
    };

    // CBP tables (mirrors H264Cbp).
    private static ReadOnlySpan<byte> GolombToInterCbp =>
    [
        0, 16, 1, 2, 4, 8, 32, 3, 5, 10, 12, 15, 47, 7, 11, 13,
        14, 6, 9, 31, 35, 37, 42, 44, 33, 34, 36, 40, 39, 43, 45, 46,
        17, 18, 20, 24, 19, 21, 26, 28, 23, 27, 29, 30, 22, 25, 38, 41,
    ];

    private static ReadOnlySpan<byte> GolombToIntraCbp =>
    [
        47, 31, 15, 0, 23, 27, 29, 30, 7, 11, 13, 14, 39, 43, 45, 46,
        16, 3, 5, 10, 12, 19, 21, 26, 28, 35, 37, 42, 44, 1, 2, 4,
        8, 17, 18, 20, 24, 6, 9, 22, 25, 32, 33, 34, 36, 40, 38, 41,
    ];
}
