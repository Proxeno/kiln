// Phase-1 acceptance test for Sonnet-tier task F2b/W2 (H264PSliceMbWriter).
//
// Authoring strategy: STRATEGY A from the F2a playbook — the real test fixtures live inside
// `#if HAS_PSLICE_MB_WRITER` blocks that reference internal types the W2 worker will add in
// `src/Kiln/Internal/H264/H264PSliceMbWriter.cs`. With that symbol UNDEFINED
// (the state at file commit time), only the
// `PSliceMbWriter_must_be_delivered_before_test_runs` fact is compiled, and it fails with a
// Sonnet-actionable error so the W2 worker's mandatory pre-check (rule 11 of the F2b common
// preamble) lights up immediately. After the W2 worker lands the production module the senior
// adds `<DefineConstants>$(DefineConstants);HAS_PSLICE_MB_WRITER</DefineConstants>` to
// `tests/Kiln.Tests/Kiln.Tests.csproj`; the gate test then short-
// circuits to a no-op pass and the real bit-exact CAVLC golden suite below activates.
//
// Drift-trap context (see h264_encoder_f2b_delegation_orchestration.md "The drift trap"):
//   A wrong mb_type table index (e.g. emitting ue(1) for P_L0_16x16 instead of ue(0)) decodes
//   into a structurally valid syntax element that the decoder then interprets as a different
//   partition shape — silently producing garbage that may or may not look broken depending on
//   the residual. The fixtures below assert the ENTIRE bitstream byte-for-byte against a
//   hand-coded oracle so a single wrong table lookup fails loudly with the exact divergence
//   point. The oracle is a small inline `GoldenBitWriter` that independently re-implements
//   WriteUe / WriteSe / WriteBit per H.264 9.1 (exp-Golomb) — it does NOT delegate to
//   `H264RbspBitBuffer` or any production code, so a bug in either side surfaces here.

using Xunit;
using System.Numerics;

#if HAS_PSLICE_MB_WRITER
using FluentAssertions;
using Kiln.Internal.H264;
using SubMbType = Kiln.Internal.H264.H264PSliceMbWriter.SubMbType;
#endif

namespace Kiln.Tests;

/// <summary>
/// Phase-1 acceptance test class for the F2b/W2 deliverable
/// (<c>Kiln.Internal.H264.H264PSliceMbWriter</c>). The class is the Sonnet-tier
/// worker's contract: it must be green by the end of W2's diff. The set of <see cref="FactAttribute"/>s
/// changes depending on the <c>HAS_PSLICE_MB_WRITER</c> compile-time symbol — see the file-level
/// comment for the strategy explanation.
/// </summary>
public sealed class H264PSliceMbWriterTests
{
#if !HAS_PSLICE_MB_WRITER
    /// <summary>
    /// Pre-delivery gate: as long as the W2 production module is missing, this test fails with
    /// a clear, actionable message so a Sonnet-tier worker reading the failure log knows
    /// exactly what to add. The full fixture suite (per-mb_type CAVLC golden bytes) lives below
    /// this method inside <c>#if HAS_PSLICE_MB_WRITER</c> and activates after the senior
    /// toggles the build symbol in phase-3 integration.
    /// </summary>
    [Fact]
    public void PSliceMbWriter_must_be_delivered_before_test_runs()
    {
        var t = Type.GetType("Kiln.Internal.H264.H264PSliceMbWriter, Kiln");
        if (t is null)
        {
            Assert.Fail(
                "W2 has not been delivered: type Kiln.Internal.H264.H264PSliceMbWriter " +
                "is missing. Implement the module per src/Kiln/Internal/H264/H264PSliceMbWriter.cs " +
                "with the public API given in the W2 Sonnet-tier prompt (`WriteMbSkipRun`, `WritePInter16x16Header`, " +
                "`WritePInter16x8Header`, `WritePInter8x16Header`, `WritePInter8x8Header`, plus the `SubMbType` enum).");
        }

        Assert.Fail(
            "W2 appears to be delivered (Kiln.Internal.H264.H264PSliceMbWriter exists), " +
            "but the test project's `HAS_PSLICE_MB_WRITER` build symbol is not enabled. The senior " +
            "must add `<DefineConstants>$(DefineConstants);HAS_PSLICE_MB_WRITER</DefineConstants>` " +
            "to a `<PropertyGroup>` in `tests/Kiln.Tests/Kiln.Tests.csproj` to " +
            "activate the real H264PSliceMbWriterTests fixtures (see file header comment).");
    }
#else
    // -----------------------------------------------------------------------------------------
    // Real fixture suite (active when HAS_PSLICE_MB_WRITER is defined).
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Independent oracle bit writer per H.264 9.1 (Exp-Golomb) and 9.1.2 (te(v)). Re-implements
    /// the spec's entropy primitives in a few lines so the test can produce expected bit
    /// sequences without calling into <see cref="H264RbspBitBuffer"/> (the production buffer).
    /// Pads the output to a 32-bit word boundary with zeros to match the production buffer's
    /// <c>Flush()</c> semantics, so the byte arrays compare byte-for-byte.
    /// </summary>
    private sealed class GoldenBitWriter
    {
        private readonly List<bool> _bits = new();

        public int BitLength => _bits.Count;

        public void WriteBit(bool b) => _bits.Add(b);

        public void WriteBits(int n, uint v)
        {
            for (var i = n - 1; i >= 0; i--)
            {
                _bits.Add(((v >> i) & 1u) == 1u);
            }
        }

        /// <summary>Per H.264 9.1: emit <c>(2·prefix + 1)</c> bits encoding <paramref name="codeNum"/>.</summary>
        public void WriteUe(uint codeNum)
        {
            var coded = codeNum + 1;
            var bits = 32 - BitOperations.LeadingZeroCount(coded);
            var leadingZeroBits = bits - 1;
            WriteBits(leadingZeroBits, 0u);
            WriteBits(bits, coded);
        }

        /// <summary>Per H.264 9.1.1: signed Exp-Golomb maps value to codeNum then ue(v).</summary>
        public void WriteSe(int value) => WriteUe(value <= 0 ? (uint)(-value * 2) : (uint)(value * 2 - 1));

        /// <summary>Per H.264 9.1.2 truncated Exp-Golomb: range==0 → no bits; range==1 → single inverted bit; else ue(v).</summary>
        public void WriteTe(int codeNum, int range)
        {
            if (range == 0)
            {
                return;
            }

            if (range == 1)
            {
                WriteBit(codeNum == 0);
                return;
            }

            WriteUe((uint)codeNum);
        }

        /// <summary>Byte-aligns the bit stream with zeros (matches H264RbspBitBuffer's corrected byte-aligned output).</summary>
        public byte[] ToBytes()
        {
            var paddedBits = (_bits.Count + 7) & ~7;
            var bytes = new byte[paddedBits / 8];
            for (var i = 0; i < _bits.Count; i++)
            {
                if (_bits[i])
                {
                    bytes[i / 8] |= (byte)(1 << (7 - (i % 8)));
                }
            }

            return bytes;
        }
    }

    /// <summary>
    /// Hand-coded sanity row: <c>WriteMbSkipRun(0)</c> writes ue(0) which per H.264 9.1 is the
    /// single bit "1". After Flush pads to a 32-bit word, the buffer must contain
    /// <c>0x80, 0x00, 0x00, 0x00</c> and <see cref="H264RbspBitBuffer.BitLength"/> must be 1.
    /// This row exists to give reviewers a wireshark-style golden they can verify by inspection
    /// without reading the GoldenBitWriter helper.
    /// </summary>
    [Fact]
    public void WriteMbSkipRun_zero_emits_single_one_bit_byte_aligned()
    {
        var bs = new H264RbspBitBuffer();
        H264PSliceMbWriter.WriteMbSkipRun(bs, 0);
        bs.BitLength.Should().Be(1, "ue(0) is the literal bit '1' per H.264 9.1");
        // Byte-aligned (1 bit → 1 byte). Previously the RBSP writer padded to a 32-bit word, appending
        // trailing zero bytes that VideoToolbox rejected.
        bs.WrittenSpan().ToArray().Should().Equal(new byte[] { 0x80 });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(256)]
    [InlineData(32_768)]
    public void WriteMbSkipRun_matches_independent_ue_oracle(int skipRun)
    {
        var bs = new H264RbspBitBuffer();
        H264PSliceMbWriter.WriteMbSkipRun(bs, skipRun);

        var golden = new GoldenBitWriter();
        golden.WriteUe((uint)skipRun);

        bs.BitLength.Should().Be(golden.BitLength,
            $"WriteMbSkipRun({skipRun}) must consume exactly the bits of ue({skipRun})");
        bs.WrittenSpan().ToArray().Should().Equal(golden.ToBytes(),
            $"WriteMbSkipRun({skipRun}) bytes must match the ue(v)-padded golden bit-for-bit");
    }

    /// <summary>
    /// Hand-coded sanity row for <c>WritePInter16x16Header(refIdx=0, mvd=(0,0), nra1=0)</c>:
    /// emits mb_type ue(0)="1", te(0, range=0) which writes nothing, mvd se(0)="1",
    /// mvd se(0)="1". Total 3 bits "111" → byte 0 = 0xE0, padding bytes 0x00.
    /// </summary>
    [Fact]
    public void WritePInter16x16Header_zero_refIdx_zero_mvd_emits_three_one_bits()
    {
        var bs = new H264RbspBitBuffer();
        H264PSliceMbWriter.WritePInter16x16Header(bs, refIdx: 0, mvdX: 0, mvdY: 0, numRefIdxActiveMinus1: 0);
        bs.BitLength.Should().Be(3,
            "mb_type ue(0)=1 + te skipped (range=0) + mvdX se(0)=1 + mvdY se(0)=1 = 3 bits");
        bs.WrittenSpan().ToArray().Should().Equal(new byte[] { 0xE0 }); // byte-aligned (3 bits → 1 byte)
    }

    public static IEnumerable<object[]> WritePInter16x16HeaderCases()
    {
        // Per the orchestration doc: 5 hand-crafted tuples covering te-skipped (nra1=0),
        // te-inverted-bit (nra1=1), full ue refIdx (nra1=2), large positive/negative mvds.
        yield return new object[] { 0, 0, 0, 0 };
        yield return new object[] { 0, 3, 3, 0 };
        yield return new object[] { 0, -3, -3, 0 };
        yield return new object[] { 1, 0, 0, 1 };
        yield return new object[] { 0, 127, -128, 2 };
    }

    [Theory]
    [MemberData(nameof(WritePInter16x16HeaderCases))]
    public void WritePInter16x16Header_matches_independent_oracle(int refIdx, int mvdX, int mvdY, int numRefIdxActiveMinus1)
    {
        var bs = new H264RbspBitBuffer();
        H264PSliceMbWriter.WritePInter16x16Header(bs, refIdx, mvdX, mvdY, numRefIdxActiveMinus1);

        var g = new GoldenBitWriter();
        g.WriteUe(0u);                               // mb_type = 0 (P_L0_16x16) per Table 7-14
        g.WriteTe(refIdx, numRefIdxActiveMinus1);    // ref_idx_l0 te(v)
        g.WriteSe(mvdX);
        g.WriteSe(mvdY);

        bs.BitLength.Should().Be(g.BitLength,
            $"P_L0_16x16(refIdx={refIdx}, mvd=({mvdX},{mvdY}), nra1={numRefIdxActiveMinus1}) bit length");
        bs.WrittenSpan().ToArray().Should().Equal(g.ToBytes(),
            $"P_L0_16x16(refIdx={refIdx}, mvd=({mvdX},{mvdY}), nra1={numRefIdxActiveMinus1}) bytes");
    }

    public static IEnumerable<object[]> WritePInter16x8HeaderCases()
    {
        // Top != bottom partitions: catches "wrote two ref_idx for the wrong partition" /
        // "wrote bottom mvd before top mvd" ordering bugs.
        yield return new object[] { 0, 0, 0, 0, 0, 0, 0 };
        yield return new object[] { 0, 0, 1, 2, -1, -2, 0 };
        yield return new object[] { 1, 0, 5, -5, 7, -7, 1 };
        yield return new object[] { 0, 1, 0, 0, 100, -100, 2 };
    }

    [Theory]
    [MemberData(nameof(WritePInter16x8HeaderCases))]
    public void WritePInter16x8Header_matches_independent_oracle(
        int refIdxTop, int refIdxBot,
        int mvdTopX, int mvdTopY,
        int mvdBotX, int mvdBotY,
        int numRefIdxActiveMinus1)
    {
        var bs = new H264RbspBitBuffer();
        H264PSliceMbWriter.WritePInter16x8Header(bs,
            refIdxTop, refIdxBot,
            mvdTopX, mvdTopY,
            mvdBotX, mvdBotY,
            numRefIdxActiveMinus1);

        var g = new GoldenBitWriter();
        g.WriteUe(1u);                                 // mb_type = 1 (P_L0_L0_16x8) per Table 7-14
        g.WriteTe(refIdxTop, numRefIdxActiveMinus1);   // 7.3.5.1: both ref_idx_l0 emitted before any mvd_l0
        g.WriteTe(refIdxBot, numRefIdxActiveMinus1);
        g.WriteSe(mvdTopX);
        g.WriteSe(mvdTopY);
        g.WriteSe(mvdBotX);
        g.WriteSe(mvdBotY);

        bs.BitLength.Should().Be(g.BitLength);
        bs.WrittenSpan().ToArray().Should().Equal(g.ToBytes(),
            $"P_L0_L0_16x8(refIdxTop={refIdxTop}, refIdxBot={refIdxBot}, mvdTop=({mvdTopX},{mvdTopY}), " +
            $"mvdBot=({mvdBotX},{mvdBotY}), nra1={numRefIdxActiveMinus1}) bytes");
    }

    public static IEnumerable<object[]> WritePInter8x16HeaderCases()
    {
        yield return new object[] { 0, 0, 0, 0, 0, 0, 0 };
        yield return new object[] { 0, 0, 1, 1, -1, -1, 0 };
        yield return new object[] { 0, 1, 0, 0, 50, -50, 1 };
        yield return new object[] { 1, 0, 9, -9, -9, 9, 2 };
    }

    [Theory]
    [MemberData(nameof(WritePInter8x16HeaderCases))]
    public void WritePInter8x16Header_matches_independent_oracle(
        int refIdxLeft, int refIdxRight,
        int mvdLeftX, int mvdLeftY,
        int mvdRightX, int mvdRightY,
        int numRefIdxActiveMinus1)
    {
        var bs = new H264RbspBitBuffer();
        H264PSliceMbWriter.WritePInter8x16Header(bs,
            refIdxLeft, refIdxRight,
            mvdLeftX, mvdLeftY,
            mvdRightX, mvdRightY,
            numRefIdxActiveMinus1);

        var g = new GoldenBitWriter();
        g.WriteUe(2u);                                  // mb_type = 2 (P_L0_L0_8x16) per Table 7-14
        g.WriteTe(refIdxLeft, numRefIdxActiveMinus1);
        g.WriteTe(refIdxRight, numRefIdxActiveMinus1);
        g.WriteSe(mvdLeftX);
        g.WriteSe(mvdLeftY);
        g.WriteSe(mvdRightX);
        g.WriteSe(mvdRightY);

        bs.BitLength.Should().Be(g.BitLength);
        bs.WrittenSpan().ToArray().Should().Equal(g.ToBytes(),
            $"P_L0_L0_8x16(refIdxLeft={refIdxLeft}, refIdxRight={refIdxRight}, " +
            $"mvdLeft=({mvdLeftX},{mvdLeftY}), mvdRight=({mvdRightX},{mvdRightY}), " +
            $"nra1={numRefIdxActiveMinus1}) bytes");
    }

    /// <summary>
    /// Homogeneous P_8x8: 4 sub-MBs, all <see cref="SubMbType.P_L0_8x8"/>, all refIdx=0, distinct
    /// per-sub-MB MVs. Tests the canonical layout per H.264 7.3.5.1 + 7.3.5.2:
    ///   mb_type ue(3),
    ///   then 4× sub_mb_type ue(0) (P_L0_8x8),
    ///   then 4× ref_idx_l0 te(v),
    ///   then 4× (mvd_l0[0] se(v), mvd_l0[1] se(v))   — one MV per sub-MB since each is 8x8.
    /// </summary>
    [Fact]
    public void WritePInter8x8Header_homogeneous_subMb_8x8_matches_independent_oracle()
    {
        var refIndices = new[] { 0, 0, 0, 0 };
        var subMbTypes = new[] { SubMbType.P_L0_8x8, SubMbType.P_L0_8x8, SubMbType.P_L0_8x8, SubMbType.P_L0_8x8 };
        var mvds = new (int X, int Y)[]
        {
            (1, 1),
            (-1, -1),
            (2, -2),
            (-3, 3),
        };

        var bs = new H264RbspBitBuffer();
        H264PSliceMbWriter.WritePInter8x8Header(bs, refIndices, subMbTypes, mvds, numRefIdxActiveMinus1: 0);

        var g = new GoldenBitWriter();
        g.WriteUe(3u);                               // mb_type = 3 (P_8x8) per Table 7-14
        for (var i = 0; i < 4; i++)
        {
            g.WriteUe((uint)(int)subMbTypes[i]);     // sub_mb_type per Table 7-17
        }
        for (var i = 0; i < 4; i++)
        {
            g.WriteTe(refIndices[i], 0);             // te skipped at range=0 (no ref_idx bits)
        }
        for (var i = 0; i < 4; i++)
        {
            g.WriteSe(mvds[i].X);
            g.WriteSe(mvds[i].Y);
        }

        bs.BitLength.Should().Be(g.BitLength);
        bs.WrittenSpan().ToArray().Should().Equal(g.ToBytes(),
            "homogeneous P_8x8 with all P_L0_8x8 sub-MBs and per-sub-MB MVs must match the spec layout");
    }

    /// <summary>
    /// Heterogeneous P_8x8: each 8×8 sub-MB picks a different sub_mb_type, exercising the
    /// per-sub-MB partition count (1 / 2 / 2 / 4 respectively → total 9 MV pairs). Catches
    /// "wrote one MV per sub-MB regardless of sub_mb_type" bugs.
    /// </summary>
    [Fact]
    public void WritePInter8x8Header_heterogeneous_subMb_types_matches_independent_oracle()
    {
        var refIndices = new[] { 0, 1, 0, 1 };
        var subMbTypes = new[]
        {
            SubMbType.P_L0_8x8, // 1 partition
            SubMbType.P_L0_8x4, // 2 partitions (top half, bottom half)
            SubMbType.P_L0_4x8, // 2 partitions (left half, right half)
            SubMbType.P_L0_4x4, // 4 partitions
        };
        var mvds = new (int X, int Y)[]
        {
            (0, 0),
            (1, 0), (1, 1),
            (2, 0), (2, 1),
            (3, 0), (3, 1), (3, 2), (3, 3),
        };

        var bs = new H264RbspBitBuffer();
        H264PSliceMbWriter.WritePInter8x8Header(bs, refIndices, subMbTypes, mvds, numRefIdxActiveMinus1: 1);

        var g = new GoldenBitWriter();
        g.WriteUe(3u);                                          // mb_type = 3 (P_8x8) per Table 7-14
        for (var i = 0; i < 4; i++)
        {
            g.WriteUe((uint)(int)subMbTypes[i]);                // sub_mb_type ue(v) per Table 7-17
        }
        for (var i = 0; i < 4; i++)
        {
            g.WriteTe(refIndices[i], 1);                         // te(v) range=1 → single inverted bit
        }
        // Per H.264 7.3.5.2: mvds emitted in scan order across (mbPartIdx, subMbPartIdx).
        var idx = 0;
        for (var i = 0; i < 4; i++)
        {
            var partitions = subMbTypes[i] switch
            {
                SubMbType.P_L0_8x8 => 1,
                SubMbType.P_L0_8x4 => 2,
                SubMbType.P_L0_4x8 => 2,
                SubMbType.P_L0_4x4 => 4,
                _ => throw new InvalidOperationException(),
            };
            for (var s = 0; s < partitions; s++)
            {
                g.WriteSe(mvds[idx].X);
                g.WriteSe(mvds[idx].Y);
                idx++;
            }
        }
        idx.Should().Be(mvds.Length, "the test fixture must consume every MV in the input span");

        bs.BitLength.Should().Be(g.BitLength);
        bs.WrittenSpan().ToArray().Should().Equal(g.ToBytes(),
            "heterogeneous P_8x8 with mixed sub_mb_types must match per-sub-MB partition expansion");
    }
#endif
}
