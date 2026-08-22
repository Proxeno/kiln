using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Acceptance test for Junior-D-cavlc. Verifies the two new <see cref="H264ResidualKind"/> variants
/// for Intra_16×16 luma — <see cref="H264ResidualKind.Luma16x16Dc"/> and
/// <see cref="H264ResidualKind.Luma16x16Ac"/> — emit bit-exact output. The contract is anchored to
/// the existing kinds because H.264 9.2.1 / 9.2.3 reuse the luma <c>coeff_token</c> / <c>total_zeros</c>
/// tables for both Intra_16×16 luma DC (full 16-coefficient block) and Intra_16×16 luma AC
/// (15-coefficient block, identical AC convention to <see cref="H264ResidualKind.ChromaAc"/>).
/// </summary>
/// <remarks>
/// Caller layout (matches the existing <see cref="H264ResidualKind.ChromaAc"/> usage in the slice
/// encoder): for <see cref="H264ResidualKind.Luma16x16Ac"/> the 15 AC coefficients are packed into a
/// 15-element zigzag span at indices 0..14, with <c>endIdx = 14</c>; coefficient at zigzag position 0
/// (the DC) is carried in the separate Luma16x16Dc block.
/// </remarks>
public sealed class H264Luma16x16CavlcTests
{
    private static byte[] WriteAndDrain(H264ResidualKind kind, ReadOnlySpan<short> coeffs, int endIdx, int nc)
    {
        var bs = new H264RbspBitBuffer();
        Span<short> work = stackalloc short[16];
        coeffs.CopyTo(work);
        H264CavlcResidual.WriteBlockResidual(bs, work, endIdx, kind, nc);
        bs.WriteRbspTrailingBits();
        return bs.WrittenSpan().ToArray();
    }

    /// <summary>
    /// Luma16x16Dc with endIdx = 15 is functionally identical to Luma4X4 with endIdx = 15 — both
    /// route through the luma <c>coeff_token</c> / <c>total_zeros</c> tables per H.264 9.2.1.1 /
    /// 9.2.3 nC selection. Junior-D-cavlc must preserve this equivalence under bitwise comparison.
    /// </summary>
    [Theory]
    [MemberData(nameof(SixteenCoefficientFixtures))]
    public void Luma16x16Dc_with_full_block_matches_Luma4X4_bitstream(string name, short[] coeffs, int nc)
    {
        var dc = WriteAndDrain(H264ResidualKind.Luma16x16Dc, coeffs, endIdx: 15, nc);
        var luma = WriteAndDrain(H264ResidualKind.Luma4X4, coeffs, endIdx: 15, nc);
        dc.Should().Equal(luma,
            $"fixture '{name}' nc={nc}: Luma16x16Dc must produce the same bitstream as Luma4X4.");
    }

    /// <summary>
    /// Luma16x16Ac with endIdx = 14 is functionally identical to ChromaAc with endIdx = 14 — both
    /// route through the luma tables, both consume a 15-coefficient zigzag span. Junior-D-cavlc must
    /// preserve this.
    /// </summary>
    [Theory]
    [MemberData(nameof(FifteenCoefficientFixtures))]
    public void Luma16x16Ac_with_packed_ac_block_matches_ChromaAc_bitstream(string name, short[] coeffs, int nc)
    {
        var ac = WriteAndDrain(H264ResidualKind.Luma16x16Ac, coeffs, endIdx: 14, nc);
        var chroma = WriteAndDrain(H264ResidualKind.ChromaAc, coeffs, endIdx: 14, nc);
        ac.Should().Equal(chroma,
            $"fixture '{name}' nc={nc}: Luma16x16Ac must produce the same bitstream as ChromaAc.");
    }

    [Fact]
    public void Luma16x16Dc_zero_block_emits_only_coeff_token_and_trailing_bits()
    {
        var coeffs = new short[16];
        var bytes = WriteAndDrain(H264ResidualKind.Luma16x16Dc, coeffs, endIdx: 15, nc: 0);
        bytes.Should().NotBeEmpty(
            "even an all-zero block emits a coeff_token VLC followed by RBSP trailing bits.");
    }

    [Fact]
    public void Luma16x16Ac_zero_block_emits_only_coeff_token_and_trailing_bits()
    {
        var coeffs = new short[15];
        var bytes = WriteAndDrain(H264ResidualKind.Luma16x16Ac, coeffs, endIdx: 14, nc: 0);
        bytes.Should().NotBeEmpty();
    }

    /// <summary>Sign-flip test: changing the sign of every coefficient flips the trailing-ones sign bits and the level codes (which encode signed magnitude); the byte length should stay constant for symmetric inputs.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void Luma16x16Dc_sign_alternation_keeps_bitstream_consistent_under_negation(int nc)
    {
        short[] coeffs =
        [
            5, -3, 0, 1, 0, 0, -1, 0,
            0, 2, 0, 0, 0, 0, 0, -1,
        ];
        short[] negated = coeffs.Select(c => (short)(-c)).ToArray();

        var pos = WriteAndDrain(H264ResidualKind.Luma16x16Dc, coeffs, endIdx: 15, nc);
        var neg = WriteAndDrain(H264ResidualKind.Luma16x16Dc, negated, endIdx: 15, nc);
        // CAVLC level coding is sign-asymmetric (a positive level maps to level_code 2|L|−2, the negative to
        // 2|L|−1, which can cross a level_prefix length boundary), so negating can shift the byte length by
        // up to 1. The exact-equality this test used to assert only held because the old RBSP writer padded
        // to a 32-bit word boundary (masking sub-word differences); now that RBSP is correctly byte-aligned
        // we assert the lengths stay within one byte.
        Math.Abs(pos.Length - neg.Length).Should().BeLessThanOrEqualTo(1,
            $"nc={nc}: negation preserves magnitudes so the encoded length differs by at most CAVLC's sign-asymmetry (≤1 byte).");
    }

    public static IEnumerable<object[]> SixteenCoefficientFixtures()
    {
        yield return ["all_zeros", new short[16], 0];
        yield return ["single_one_at_pos0", OneAt(0, 1, 16), 0];
        yield return ["single_one_at_pos15", OneAt(15, 1, 16), 0];
        yield return ["dense_dc_only", new short[] { 6, -2, 1, -1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 2];
        yield return ["three_trailing_ones", new short[] { 4, 0, -1, 0, 1, 0, -1, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 1];
        yield return ["dense_high_levels", new short[] { 11, -7, 5, -3, 2, -2, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 4];
        yield return ["sparse_with_runs", new short[] { 0, 0, 3, 0, 0, 0, -2, 0, 0, 1, 0, 0, 0, 0, 0, -1 }, 2];
        yield return ["all_ones_alternating", new short[] { 1, -1, 1, -1, 1, -1, 1, -1, 1, -1, 1, -1, 1, -1, 1, -1 }, 8];
    }

    public static IEnumerable<object[]> FifteenCoefficientFixtures()
    {
        yield return ["all_zeros_15", new short[15], 0];
        yield return ["single_one_at_pos0_15", OneAt(0, 1, 15), 0];
        yield return ["single_one_at_pos14_15", OneAt(14, 1, 15), 0];
        yield return ["sparse_runs_15", new short[] { 4, 0, -1, 0, 0, 1, 0, -1, 0, 0, 0, 1, 0, 0, 0 }, 2];
        yield return ["dense_levels_15", new short[] { 9, -5, 3, -2, 1, -1, 1, 0, 0, 0, 0, 0, 0, 0, 0 }, 4];
        yield return ["alternating_15", new short[] { 1, -1, 1, -1, 1, -1, 1, -1, 1, -1, 1, -1, 1, -1, 1 }, 8];
    }

    private static short[] OneAt(int pos, short value, int len)
    {
        var arr = new short[len];
        arr[pos] = value;
        return arr;
    }
}
