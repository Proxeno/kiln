using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Exercises <see cref="H264CavlcResidual.WriteBlockResidual"/> via H.264 §9.2 decode in
/// <see cref="H264CavlcSpecDecode"/> — no stored goldens, no ffmpeg.
/// </summary>
public sealed class H264CavlcSpecRoundtripTests
{
    private static void AssertRoundTrip(short[] coeffs, int endIdx, H264ResidualKind kind, int nc)
    {
        var isChromaDc = kind == H264ResidualKind.ChromaDc;
        var bytes = H264CavlcSpecDecode.EncodeBlock(coeffs, endIdx, kind, nc);
        var br = new H264CavlcSpecDecode.BitReader(bytes);
        var decoded = H264CavlcSpecDecode.DecodeBlock(br, endIdx, nc, isChromaDc);
        decoded.Should().Equal(
            coeffs.Take(endIdx + 1),
            $"kind={kind} endIdx={endIdx} nc={nc} coeffs=[{string.Join(",", coeffs.Take(endIdx + 1))}]");
    }

    private static short[] Zeros(int len) => new short[len];

    // --- 1. coeff_token zero block ---

    public static IEnumerable<object[]> ZeroBlockKindsAndNc()
    {
        var ncs = new[] { -1, 0, 1, 2, 4, 8, 15, 16, 17, 31 };
        foreach (var nc in ncs)
        {
            yield return [(int)H264ResidualKind.Luma4X4, 15, nc];
            yield return [(int)H264ResidualKind.ChromaAc, 14, nc];
            yield return [(int)H264ResidualKind.Luma16x16Dc, 15, nc];
            yield return [(int)H264ResidualKind.Luma16x16Ac, 14, nc];
        }

        yield return [(int)H264ResidualKind.ChromaDc, 3, 0];
    }

    [Theory]
    [MemberData(nameof(ZeroBlockKindsAndNc))]
    public void CoeffToken_zero_block_roundtrips(int kindOrdinal, int endIdx, int nc)
    {
        var kind = (H264ResidualKind)kindOrdinal;
        AssertRoundTrip(new short[endIdx + 1], endIdx, kind, nc);
    }

    // --- 2. trailing ones branches ---

    public static IEnumerable<object[]> TrailingOnesCases()
    {
        foreach (var tc in new[] { 1, 2, 3, 5, 11 })
            foreach (var t1 in new[] { 0, 1, 2, 3 })
                if (t1 <= tc)
                    yield return [tc, t1];
    }

    [Theory]
    [MemberData(nameof(TrailingOnesCases))]
    public void Trailing_ones_count_branches(int totalCoeffs, int trailingOnes)
    {
        var c = Zeros(16);
        var i = 15;
        for (var j = 0; j < trailingOnes; j++, i--)
            c[i] = ((j & 1) == 0) ? (short)1 : (short)-1;

        for (var k = 0; k < totalCoeffs - trailingOnes; k++, i--)
            c[i] = (short)((k % 2 == 0) ? 2 : -3);

        AssertRoundTrip(c, 15, H264ResidualKind.Luma4X4, 0);
    }

    // --- 3. suffixLength initial ---

    [Fact]
    public void SuffixLength_initial_branch_tc11_trailing0_starts_suffixLength_at_1()
    {
        var c = Zeros(16);
        var idx = 15;
        for (var i = 0; i < 11; i++)
            c[idx--] = 2;
        AssertRoundTrip(c, 15, H264ResidualKind.Luma4X4, 8);
    }

    [Fact]
    public void SuffixLength_initial_branch_tc11_trailing3_starts_suffixLength_at_0()
    {
        var c = Zeros(16);
        c[15] = 1;
        c[14] = -1;
        c[13] = 1;
        var idx = 12;
        for (var i = 0; i < 8; i++)
            c[idx--] = 2;
        AssertRoundTrip(c, 15, H264ResidualKind.Luma4X4, 8);
    }

    [Fact]
    public void SuffixLength_initial_branch_tc10_starts_suffixLength_at_0()
    {
        var c = Zeros(16);
        var idx = 15;
        for (var i = 0; i < 10; i++)
            c[idx--] = 2;
        AssertRoundTrip(c, 15, H264ResidualKind.Luma4X4, 2);
    }

    // --- 4. suffixLength threshold progression (sl after first level) ---

    [Theory]
    [InlineData(1)] // thresh 3
    [InlineData(2)] // thresh 6
    [InlineData(3)] // thresh 12
    [InlineData(4)] // thresh 24
    [InlineData(5)] // thresh 48
    public void SuffixLength_threshold_progression_second_coeff_uses_expected_width(int tier)
    {
        var threshAfterFirst = 3 << (tier - 1);
        var c = Zeros(16);
        c[15] = (short)(threshAfterFirst + 1);
        c[14] = 1;
        c[13] = (short)((threshAfterFirst + 8) >> 2);
        AssertRoundTrip(c, 15, H264ResidualKind.Luma4X4, 0);
    }

    // --- 5–7. Level escapes ---

    [Fact]
    public void Level_prefix_14_escape_when_suffixLength_initially_zero()
    {
        for (var v = 9; v <= 16; v++)
        {
            var c = Zeros(16);
            c[15] = (short)v;
            AssertRoundTrip(c, 15, H264ResidualKind.Luma4X4, 0);
            c[15] = (short)-v;
            AssertRoundTrip(c, 15, H264ResidualKind.Luma4X4, 0);
        }
    }

    [Fact]
    public void Level_prefix_15_escape_when_suffixLength_initially_zero()
    {
        foreach (var v in new[] { 17, 20, 29, 50, 100, 500 })
        {
            var c = Zeros(16);
            c[15] = (short)v;
            AssertRoundTrip(c, 15, H264ResidualKind.Luma4X4, 0);
            c[15] = (short)-v;
            AssertRoundTrip(c, 15, H264ResidualKind.Luma4X4, 0);
        }
    }

    [Fact]
    public void Level_prefix_15_when_suffixLength_positive_no_minus15_on_suffix()
    {
        var c = Zeros(16);
        c[15] = 1;
        c[14] = -1;
        c[13] = 1;
        c[12] = 100;
        AssertRoundTrip(c, 15, H264ResidualKind.Luma4X4, 0);
    }

    // --- 8. first-non-trailing -2 adjustment ---

    public static IEnumerable<object[]> FirstNtMinus2_TrailingCounts()
    {
        yield return [0];
        yield return [1];
        yield return [2];
    }

    [Theory]
    [MemberData(nameof(FirstNtMinus2_TrailingCounts))]
    public void FirstNonTrailing_minus2_adjustment(int trailingOnes)
    {
        var c = Zeros(16);
        var idx = 15;
        for (var j = 0; j < trailingOnes; j++, idx--)
            c[idx] = ((j & 1) == 0) ? (short)1 : (short)-1;
        c[idx] = 2;
        AssertRoundTrip(c, 15, H264ResidualKind.Luma4X4, 4);
    }

    // --- 9. run_before + ZeroLeft clamp (zerosLeft large) ---

    [Fact]
    public void RunBefore_zerosLeft_uses_table_index_clamp_at_7()
    {
        var c = Zeros(16);
        c[15] = 3;
        c[6] = -2;
        AssertRoundTrip(c, 15, H264ResidualKind.Luma4X4, 0);
    }

    [Fact]
    public void RunBefore_multiple_runs_with_small_zerosLeft()
    {
        var c = Zeros(16);
        c[15] = 1;
        c[12] = 2;
        c[8] = -1;
        AssertRoundTrip(c, 15, H264ResidualKind.Luma4X4, 0);
    }

    // --- 10. total_zeros skipped when block full ---

    public static IEnumerable<object[]> FullBlockKinds()
    {
        yield return [(int)H264ResidualKind.Luma4X4, 15];
        yield return [(int)H264ResidualKind.Luma16x16Dc, 15];
        yield return [(int)H264ResidualKind.ChromaAc, 14];
        yield return [(int)H264ResidualKind.Luma16x16Ac, 14];
    }

    [Theory]
    [MemberData(nameof(FullBlockKinds))]
    public void TotalZeros_skipped_when_totalCoeffs_fills_block(int kindOrdinal, int endIdx)
    {
        var kind = (H264ResidualKind)kindOrdinal;
        var c = new short[16];
        for (var i = 0; i <= endIdx; i++)
            c[i] = (short)(((i & 1) == 0) ? 1 : -2);
        AssertRoundTrip(c, endIdx, kind, 8);
    }

    [Fact]
    public void TotalZeros_skipped_chroma_dc_all_four_nonzero()
    {
        var c = new short[] { 1, -2, 3, -1 };
        AssertRoundTrip(c, 3, H264ResidualKind.ChromaDc, 0);
    }

    // --- 11. sign symmetry / bit length ---

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(15)]
    public void Sign_symmetry_roundtrips_for_single_coeff(int mag)
    {
        var p = Zeros(16);
        p[15] = (short)mag;
        var n = Zeros(16);
        n[15] = (short)-mag;
        AssertRoundTrip(p, 15, H264ResidualKind.Luma4X4, 0);
        AssertRoundTrip(n, 15, H264ResidualKind.Luma4X4, 0);
    }

    // --- 12. fuzz per kind ---

    [Fact]
    public void Fuzz_Luma4X4()
    {
        var rng = new Random(0x4C14);
        for (var t = 0; t < 2000; t++)
        {
            var c = Zeros(16);
            var nz = rng.Next(0, 17);
            for (var i = 0; i < nz; i++)
            {
                var pos = rng.Next(0, 16);
                var mag = rng.Next(1, 129);
                c[pos] = (short)(rng.Next(2) == 0 ? mag : -mag);
            }

            var nc = rng.Next(0, 17) switch
            {
                var x when x % 3 == 0 => 0,
                var x when x % 3 == 1 => 4,
                _ => 16,
            };
            AssertRoundTrip(c, 15, H264ResidualKind.Luma4X4, nc);
        }
    }

    [Fact]
    public void Fuzz_ChromaAc_and_Luma16x16Ac()
    {
        var rng = new Random(0xAC14);
        for (var t = 0; t < 2000; t++)
        {
            var c = Zeros(16);
            var nz = rng.Next(0, 16);
            for (var i = 0; i < nz; i++)
            {
                var pos = rng.Next(0, 15);
                var mag = rng.Next(1, 129);
                c[pos] = (short)(rng.Next(2) == 0 ? mag : -mag);
            }

            var nc = rng.Next(0, 17) switch
            {
                var x when x % 3 == 0 => 0,
                var x when x % 3 == 1 => 4,
                _ => 16,
            };
            AssertRoundTrip(c, 14, H264ResidualKind.ChromaAc, nc);
            AssertRoundTrip(c, 14, H264ResidualKind.Luma16x16Ac, nc);
        }
    }

    [Fact]
    public void Fuzz_Luma16x16Dc()
    {
        var rng = new Random(0xDC16);
        for (var t = 0; t < 2000; t++)
        {
            var c = Zeros(16);
            var nz = rng.Next(0, 17);
            for (var i = 0; i < nz; i++)
            {
                var pos = rng.Next(0, 16);
                var mag = rng.Next(1, 129);
                c[pos] = (short)(rng.Next(2) == 0 ? mag : -mag);
            }

            var nc = rng.Next(0, 17) switch
            {
                var x when x % 3 == 0 => 0,
                var x when x % 3 == 1 => 4,
                _ => 16,
            };
            AssertRoundTrip(c, 15, H264ResidualKind.Luma16x16Dc, nc);
        }
    }

    [Fact]
    public void Fuzz_ChromaDc()
    {
        var rng = new Random(0xDC04);
        for (var t = 0; t < 2000; t++)
        {
            var c = new short[4];
            for (var i = 0; i < 4; i++)
                c[i] = (short)rng.Next(-200, 201);
            AssertRoundTrip(c, 3, H264ResidualKind.ChromaDc, 0);
        }
    }

    // --- coverage: single coeff every position / magnitudes ---

    [Fact]
    public void Single_nonzero_at_each_index_luma4x4()
    {
        foreach (var pos in Enumerable.Range(0, 16))
            foreach (var val in new short[] { 1, -1, 2, -2, 5, -7, 9, -17 })
            {
                var c = Zeros(16);
                c[pos] = val;
                AssertRoundTrip(c, 15, H264ResidualKind.Luma4X4, 0);
            }
    }

    [Fact]
    public void Full_block_all_nonzero_alternating()
    {
        var c = new short[]
        {
            1, -1, 2, -2, 3, -3, 4, -4, 5, -5, 6, -6, 7, -7, 8, -8,
        };
        AssertRoundTrip(c, 15, H264ResidualKind.Luma4X4, 8);
    }

    // --- cross-block invariants ---

    [Fact]
    public void Encoded_bit_length_is_byte_aligned_after_rbsp_trailing_bits()
    {
        var bs = new H264RbspBitBuffer();
        Span<short> w = stackalloc short[16];
        w[15] = 4;
        H264CavlcResidual.WriteBlockResidual(bs, w, 15, H264ResidualKind.Luma4X4, 0);
        bs.WriteRbspTrailingBits();
        (bs.BitLength % 8).Should().Be(0);
    }

    [Fact]
    public void Two_back_to_back_blocks_decode_independently()
    {
        var bs = new H264RbspBitBuffer();
        var a = Zeros(16);
        a[15] = 5;
        var b = Zeros(16);
        b[14] = -3;
        b[15] = 1;
        H264CavlcSpecDecode.EncodeBlockNoTrailing(bs, a, 15, H264ResidualKind.Luma4X4, 2);
        H264CavlcSpecDecode.EncodeBlockNoTrailing(bs, b, 15, H264ResidualKind.Luma4X4, 4);
        bs.WriteRbspTrailingBits();
        var bytes = bs.WrittenSpan().ToArray();
        var br = new H264CavlcSpecDecode.BitReader(bytes);
        var d1 = H264CavlcSpecDecode.DecodeBlock(br, 15, 2, false);
        var d2 = H264CavlcSpecDecode.DecodeBlock(br, 15, 4, false);
        d1.Should().Equal(a.Take(16));
        d2.Should().Equal(b.Take(16));
    }
}
