// Tests for Intra_16×16 macroblock bitstream writing.
//
// The tests are gated behind HAS_INTRA16x16_WRITER so the project builds before the
// production module is delivered; enabling the symbol activates the full fixture suite.

using Xunit;

#if HAS_INTRA16x16_WRITER
using FluentAssertions;
using System.Numerics;
using Kiln.Internal.H264;
#endif

namespace Kiln.Tests;

/// <summary>
/// Parity tests for Intra_16×16 macroblock header encoding.
///
/// H.264 Table 7-11/7-14 column order is (predMode, cbpChroma, cbpLumaBit), so
/// the codeNum formula is:
///   I-slice: mb_type = 1 + predMode + 4 × cbpChroma + 12 × cbpLumaBit
///   P-slice: mb_type = 5 + (same)  (the +5 offset accounts for the five P_* entries)
/// where:
///   predMode  ∈ {0=Vertical, 1=Horizontal, 2=DC, 3=Plane}
///   cbpChroma ∈ {0=none, 1=DC only, 2=AC+DC}
///   cbpLumaBit∈ {0=no AC, 1=has AC}  (the bit form of CodedBlockPatternLuma ∈ {0,15})
/// </summary>
public sealed class H264Intra16x16MbWriterTests
{
#if !HAS_INTRA16x16_WRITER
    [Fact]
    public void Intra16x16Writer_must_be_delivered_before_test_runs()
    {
        var t = Type.GetType("Kiln.Internal.H264.H264SliceMbWriter, Kiln");
        if (t is null)
        {
            Assert.Fail(
                "H264SliceMbWriter is missing. Implement WriteIntra16x16Macroblock per the plan.");
        }

        var m = t.GetMethod("WriteIntra16x16Macroblock",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (m is null)
        {
            Assert.Fail(
                "H264SliceMbWriter.WriteIntra16x16Macroblock is missing. " +
                "Enable HAS_INTRA16x16_WRITER in Kiln.Tests.csproj once it is delivered.");
        }

        Assert.Fail(
            "H264SliceMbWriter.WriteIntra16x16Macroblock appears to exist but HAS_INTRA16x16_WRITER is " +
            "not set. Add it to DefineConstants in Kiln.Tests.csproj.");
    }
#else
    // ─── Independent oracle (same pattern as H264PSliceMbWriterTests.GoldenBitWriter) ───────────
    private sealed class GoldenBitWriter
    {
        private readonly System.Collections.Generic.List<bool> _bits = new();
        public int BitLength => _bits.Count;
        public void WriteBit(bool b) => _bits.Add(b);
        public void WriteBits(int n, uint v)
        {
            for (var i = n - 1; i >= 0; i--) _bits.Add(((v >> i) & 1u) == 1u);
        }
        public void WriteUe(uint codeNum)
        {
            var coded = codeNum + 1;
            var bits = 32 - BitOperations.LeadingZeroCount(coded);
            WriteBits(bits - 1, 0u);
            WriteBits(bits, coded);
        }
        public void WriteSe(int v) => WriteUe(v <= 0 ? (uint)(-v * 2) : (uint)(v * 2 - 1));
        public byte[] ToBytes()
        {
            var padded = (_bits.Count + 7) & ~7; // byte-align (matches the corrected RBSP writer; was 32-bit word-padded)
            var b = new byte[padded / 8];
            for (var i = 0; i < _bits.Count; i++)
                if (_bits[i]) b[i / 8] |= (byte)(1 << (7 - (i % 8)));
            return b;
        }
    }

    /// <summary>H.264 Table 7-14 codeNum for I_16×16 in a P-slice.</summary>
    private static uint I16x16CodeNum(int predMode, int cbpLuma, int cbpChroma)
        => (uint)(5 + 1 + predMode + 4 * cbpChroma + 12 * cbpLuma);

    public static System.Collections.Generic.IEnumerable<object[]> MbTypeCases()
    {
        // Exhaustive over all 4 × 2 × 3 = 24 combinations.
        for (var predMode = 0; predMode < 4; predMode++)
        for (var cbpLuma = 0; cbpLuma <= 1; cbpLuma++)
        for (var cbpChroma = 0; cbpChroma <= 2; cbpChroma++)
            yield return new object[] { predMode, cbpLuma, cbpChroma };
    }

    [Theory]
    [MemberData(nameof(MbTypeCases))]
    public void WriteIntra16x16Header_mb_type_matches_Table7_14_formula(
        int predMode, int cbpLuma, int cbpChroma)
    {
        var bs = new H264RbspBitBuffer();
        H264SliceMbWriter.WriteIntra16x16Header(bs, predMode, cbpLuma, cbpChroma, isPSlice: true);

        var g = new GoldenBitWriter();
        g.WriteUe(I16x16CodeNum(predMode, cbpLuma, cbpChroma));

        bs.BitLength.Should().Be(g.BitLength,
            $"I16x16 predMode={predMode} cbpLuma={cbpLuma} cbpChroma={cbpChroma}: mb_type bit length");
        bs.WrittenSpan().ToArray().Should().Equal(g.ToBytes(),
            $"I16x16 predMode={predMode} cbpLuma={cbpLuma} cbpChroma={cbpChroma}: mb_type bytes");
    }

    /// <summary>Hand-coded sanity row: DC mode, no cbp → mb_type codeNum = 5+1+2+0+0 = 8 = ue(8).</summary>
    [Fact]
    public void WriteIntra16x16Header_dc_mode_no_cbp_emits_ue_8()
    {
        var bs = new H264RbspBitBuffer();
        H264SliceMbWriter.WriteIntra16x16Header(bs, predMode: 2, cbpLuma: 0, cbpChroma: 0, isPSlice: true);

        var g = new GoldenBitWriter();
        g.WriteUe(8u); // 5+1+2+0+0=8

        bs.BitLength.Should().Be(g.BitLength);
        bs.WrittenSpan().ToArray().Should().Equal(g.ToBytes(),
            "DC mode, no cbp: Table 7-14 codeNum = 8");
    }

    /// <summary>I-slice context: mb_type = 1 + predMode + 4×cbpLuma + 12×cbpChroma (no +5 offset).</summary>
    [Theory]
    [MemberData(nameof(MbTypeCases))]
    public void WriteIntra16x16Header_i_slice_mb_type_has_no_p_slice_offset(
        int predMode, int cbpLuma, int cbpChroma)
    {
        var bs = new H264RbspBitBuffer();
        H264SliceMbWriter.WriteIntra16x16Header(bs, predMode, cbpLuma, cbpChroma, isPSlice: false);

        var g = new GoldenBitWriter();
        g.WriteUe((uint)(1 + predMode + 4 * cbpChroma + 12 * cbpLuma)); // Table 7-11

        bs.BitLength.Should().Be(g.BitLength);
        bs.WrittenSpan().ToArray().Should().Equal(g.ToBytes(),
            $"I-slice I16x16 predMode={predMode} cbpLuma={cbpLuma} cbpChroma={cbpChroma}");
    }
#endif
}
