using System;

namespace Kiln.Internal.H264;

/// <summary>
/// CAVLC writers for P-slice macroblock syntax per H.264 7.3.5.1 (macroblock_layer) and 7.3.5.2 (sub_mb_pred).
/// Pure functions; consume an existing <see cref="H264RbspBitBuffer"/> bitstream and append the encoded syntax.
/// </summary>
internal static class H264PSliceMbWriter
{
    /// <summary>Sub-MB partition types for P_8x8 per H.264 Table 7-17.</summary>
    public enum SubMbType : byte
    {
        P_L0_8x8 = 0,
        P_L0_8x4 = 1,
        P_L0_4x8 = 2,
        P_L0_4x4 = 3,
    }

    /// <summary>
    /// Per H.264 7.3.4 — P-slice mb_skip_run is the run-length of P_Skip MBs preceding the next
    /// non-skip MB. Emits a single <c>ue(v)</c> encoding the skip count. The encoder accumulates
    /// the run; this function merely serialises it.
    /// </summary>
    public static void WriteMbSkipRun(H264RbspBitBuffer bs, int skipRun)
    {
        if (skipRun < 0) throw new ArgumentOutOfRangeException(nameof(skipRun));
        bs.WriteUe((uint)skipRun);
    }

    /// <summary>
    /// Per H.264 7.3.5.1 + Table 7-14:
    /// <code>
    /// mb_type | Name           | NumMbPart | MbPartWidth | MbPartHeight | MbPartPredMode | inferred_cbp
    /// 0       | P_L0_16x16     | 1         | 16          | 16           | Pred_L0        | n/a
    /// </code>
    /// Emits: mb_type ue(0), ref_idx_l0 te(v), mvd_l0[0] se(v), mvd_l0[1] se(v).
    /// </summary>
    public static void WritePInter16x16Header(
        H264RbspBitBuffer bs, int refIdx, int mvdX, int mvdY, int numRefIdxActiveMinus1)
    {
        if (refIdx < 0) throw new ArgumentOutOfRangeException(nameof(refIdx));
        bs.WriteUe(0u);                                      // mb_type = 0 (P_L0_16x16)
        WriteTe(bs, refIdx, numRefIdxActiveMinus1);          // ref_idx_l0 te(v)
        bs.WriteSe(mvdX);                                    // mvd_l0[0]
        bs.WriteSe(mvdY);                                    // mvd_l0[1]
    }

    /// <summary>
    /// Per H.264 7.3.5.1 + Table 7-14:
    /// <code>
    /// mb_type | Name           | NumMbPart | MbPartWidth | MbPartHeight | MbPartPredMode | inferred_cbp
    /// 1       | P_L0_L0_16x8   | 2         | 16          | 8            | Pred_L0        | n/a
    /// </code>
    /// Emits: mb_type ue(1), ref_idx_l0 te(v)×2 (top then bottom), mvd_l0 se(v)×4 (top-x, top-y, bot-x, bot-y).
    /// Per H.264 7.3.5.1 both ref_idx_l0 values precede all mvd_l0 values (ref_pred_weight_table ordering).
    /// </summary>
    public static void WritePInter16x8Header(
        H264RbspBitBuffer bs,
        int refIdxTop, int refIdxBot,
        int mvdTopX, int mvdTopY,
        int mvdBotX, int mvdBotY,
        int numRefIdxActiveMinus1)
    {
        if (refIdxTop < 0) throw new ArgumentOutOfRangeException(nameof(refIdxTop));
        if (refIdxBot < 0) throw new ArgumentOutOfRangeException(nameof(refIdxBot));
        bs.WriteUe(1u);                                      // mb_type = 1 (P_L0_L0_16x8)
        WriteTe(bs, refIdxTop, numRefIdxActiveMinus1);       // ref_idx_l0[top]
        WriteTe(bs, refIdxBot, numRefIdxActiveMinus1);       // ref_idx_l0[bottom]
        bs.WriteSe(mvdTopX);                                 // mvd_l0[0][top] x
        bs.WriteSe(mvdTopY);                                 // mvd_l0[0][top] y
        bs.WriteSe(mvdBotX);                                 // mvd_l0[0][bottom] x
        bs.WriteSe(mvdBotY);                                 // mvd_l0[0][bottom] y
    }

    /// <summary>
    /// Per H.264 7.3.5.1 + Table 7-14:
    /// <code>
    /// mb_type | Name           | NumMbPart | MbPartWidth | MbPartHeight | MbPartPredMode | inferred_cbp
    /// 2       | P_L0_L0_8x16   | 2         | 8           | 16           | Pred_L0        | n/a
    /// </code>
    /// Emits: mb_type ue(2), ref_idx_l0 te(v)×2 (left then right), mvd_l0 se(v)×4 (left-x, left-y, right-x, right-y).
    /// </summary>
    public static void WritePInter8x16Header(
        H264RbspBitBuffer bs,
        int refIdxLeft, int refIdxRight,
        int mvdLeftX, int mvdLeftY,
        int mvdRightX, int mvdRightY,
        int numRefIdxActiveMinus1)
    {
        if (refIdxLeft < 0) throw new ArgumentOutOfRangeException(nameof(refIdxLeft));
        if (refIdxRight < 0) throw new ArgumentOutOfRangeException(nameof(refIdxRight));
        bs.WriteUe(2u);                                      // mb_type = 2 (P_L0_L0_8x16)
        WriteTe(bs, refIdxLeft, numRefIdxActiveMinus1);      // ref_idx_l0[left]
        WriteTe(bs, refIdxRight, numRefIdxActiveMinus1);     // ref_idx_l0[right]
        bs.WriteSe(mvdLeftX);                                // mvd_l0[0][left] x
        bs.WriteSe(mvdLeftY);                                // mvd_l0[0][left] y
        bs.WriteSe(mvdRightX);                               // mvd_l0[0][right] x
        bs.WriteSe(mvdRightY);                               // mvd_l0[0][right] y
    }

    /// <summary>
    /// Per H.264 7.3.5.1 + 7.3.5.2 + Table 7-14 + Table 7-17:
    /// <code>
    /// mb_type | Name    | NumMbPart | MbPartWidth | MbPartHeight | MbPartPredMode | inferred_cbp
    /// 3       | P_8x8   | 4         | 8           | 8            | per sub_mb     | n/a
    ///
    /// sub_mb_type | Name        | NumSubMbPart | SubMbPartWidth | SubMbPartHeight
    /// 0           | P_L0_8x8    | 1            | 8              | 8
    /// 1           | P_L0_8x4    | 2            | 8              | 4
    /// 2           | P_L0_4x8    | 2            | 4              | 8
    /// 3           | P_L0_4x4    | 4            | 4              | 4
    /// </code>
    /// Emits per H.264 7.3.5.1 + 7.3.5.2 in order:
    ///   mb_type ue(3),
    ///   4× sub_mb_type ue(v),
    ///   4× ref_idx_l0 te(v),
    ///   for each 8×8 sub-MB, for each sub-partition (in mbPartIdx, subMbPartIdx scan order): mvd_l0[0] se(v), mvd_l0[1] se(v).
    /// <paramref name="mvds"/> must contain exactly Sum(NumSubMbPartitions(subMbTypes[i])) tuples in scan order.
    /// </summary>
    public static void WritePInter8x8Header(
        H264RbspBitBuffer bs,
        ReadOnlySpan<int> refIndices,
        ReadOnlySpan<SubMbType> subMbTypes,
        ReadOnlySpan<(int X, int Y)> mvds,
        int numRefIdxActiveMinus1)
    {
        if (refIndices.Length != 4) throw new ArgumentOutOfRangeException(nameof(refIndices));
        if (subMbTypes.Length != 4) throw new ArgumentOutOfRangeException(nameof(subMbTypes));
        for (var i = 0; i < 4; i++)
        {
            if (refIndices[i] < 0) throw new ArgumentOutOfRangeException(nameof(refIndices));
            if ((uint)(int)subMbTypes[i] > 3u) throw new ArgumentOutOfRangeException(nameof(subMbTypes));
        }

        bs.WriteUe(3u);                                      // mb_type = 3 (P_8x8) per Table 7-14

        // sub_mb_pred: emit all sub_mb_type values before any ref_idx or mvd per 7.3.5.2
        for (var i = 0; i < 4; i++)
        {
            bs.WriteUe((uint)(int)subMbTypes[i]);            // sub_mb_type ue(v) per Table 7-17
        }

        // ref_idx_l0 for each 8×8 sub-MB (one per sub-MB partition, not per sub-sub-MB)
        for (var i = 0; i < 4; i++)
        {
            WriteTe(bs, refIndices[i], numRefIdxActiveMinus1);
        }

        // mvd_l0 in (mbPartIdx, subMbPartIdx) scan order; per-sub-partition count comes from sub_mb_type
        var mvIdx = 0;
        for (var i = 0; i < 4; i++)
        {
            var partitions = NumSubMbPartitions(subMbTypes[i]);
            for (var p = 0; p < partitions; p++)
            {
                bs.WriteSe(mvds[mvIdx].X);                   // mvd_l0[mbPartIdx][subMbPartIdx][0]
                bs.WriteSe(mvds[mvIdx].Y);                   // mvd_l0[mbPartIdx][subMbPartIdx][1]
                mvIdx++;
            }
        }
    }

    /// <summary>
    /// H.264 9.1.2 truncated Exp-Golomb te(v) for ref_idx_l0:
    /// <list type="bullet">
    ///   <item><paramref name="range"/> == 0 (single reference): no bits emitted.</item>
    ///   <item><paramref name="range"/> == 1 (two references): single inverted bit (1 − codeNum).</item>
    ///   <item><paramref name="range"/> ≥ 2: WriteUe(codeNum).</item>
    /// </list>
    /// </summary>
    private static void WriteTe(H264RbspBitBuffer bs, int codeNum, int range)
    {
        if (range == 0) return;                              // single ref: no choice, no bits
        if (range == 1) { bs.WriteBit(codeNum == 0); return; } // inverted single bit per 9.1.2
        bs.WriteUe((uint)codeNum);
    }

    /// <summary>
    /// Returns the number of sub-MB partitions (and thus MV pairs) for a given <see cref="SubMbType"/>
    /// per H.264 Table 7-17 NumSubMbPart column.
    /// </summary>
    private static int NumSubMbPartitions(SubMbType t) => t switch
    {
        SubMbType.P_L0_8x8 => 1,
        SubMbType.P_L0_8x4 => 2,
        SubMbType.P_L0_4x8 => 2,
        SubMbType.P_L0_4x4 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };
}
