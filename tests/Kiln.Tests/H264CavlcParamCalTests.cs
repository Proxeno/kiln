using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

public sealed class H264CavlcParamCalTests
{
    [Fact]
    public void CavlcParamCal_totalCoeffs_equals_TotalCoefficients_helper()
    {
        var rnd = new Random(0xC441);
        Span<short> c = stackalloc short[16];
        Span<short> level = stackalloc short[16];
        Span<byte> run = stackalloc byte[16];

        for (var t = 0; t < 500; t++)
        {
            c.Clear();
            var lastIdx = rnd.Next(0, 16);
            for (var nz = rnd.Next(0, 9); nz > 0; nz--)
                c[rnd.Next(0, lastIdx + 1)] = (short)rnd.Next(-60, 60);

            H264CavlcResidual.CavlcParamCal(c, lastIdx, level, run, out var tcExpected, out _);
            H264CavlcResidual.TotalCoefficients(c, lastIdx).Should().Be(tcExpected, $"trial={t} lastIdx={lastIdx}");
        }
    }

    [Fact]
    public void CavlcParamCal_all_zero_region_yields_tc0()
    {
        var c = new short[16];
        Span<short> level = stackalloc short[16];
        Span<byte> run = stackalloc byte[16];
        H264CavlcResidual.CavlcParamCal(c, lastIndex: 15, level, run, out var tc, out var tz);
        tc.Should().Be(0);
        tz.Should().Be(0);
    }

    [Fact]
    public void CavlcParamCal_single_non_zero_at_hf()
    {
        var c = new short[16];
        c[15] = -13;
        Span<short> level = stackalloc short[16];
        Span<byte> run = stackalloc byte[16];
        H264CavlcResidual.CavlcParamCal(c, lastIndex: 15, level, run, out var tc, out var tz);
        tc.Should().Be(1);
        tz.Should().Be(15);
        level[0].Should().Be(-13);
        run[0].Should().Be(15); // zeros at indices14..0 in reverse scan toward DC
    }

    [Fact]
    public void CavlcParamCal_hf_pair_with_interior_gap()
    {
        // Non-zero only at zigzag indexes 13 and 15 → one coefficient zero between → totalZeros(run[0]=1 second coeff)
        var c = new short[16];
        c[13] = 5;
        c[15] = -2;

        Span<short> level = stackalloc short[16];
        Span<byte> run = stackalloc byte[16];
        H264CavlcResidual.CavlcParamCal(c, lastIndex: 15, level, run, out var tc, out var tz);
        tc.Should().Be(2);
        tz.Should().Be(14); // gap of 1 at idx14 plus 13 LF zeros before idx13
        level[0].Should().Be(-2); // scanned from index 15 first
        level[1].Should().Be(5);
        run[0].Should().Be(1);
        run[1].Should().Be(13);
    }

    [Fact]
    public void CavlcParamCal_last_index_trimming_trailing_zero_tail_matches_full_scan()
    {
        var c = new short[16];
        c[14] = 3;
        c[15] = 0;

        Span<short> lShort = stackalloc short[16];
        Span<byte> rShort = stackalloc byte[16];
        H264CavlcResidual.CavlcParamCal(c.AsSpan(), lastIndex: 14, lShort, rShort, out var tcS, out var tzS);

        Span<short> lFull = stackalloc short[16];
        Span<byte> rFull = stackalloc byte[16];
        H264CavlcResidual.CavlcParamCal(c.AsSpan(), lastIndex: 15, lFull, rFull, out var tcL, out var tzL);

        tcS.Should().Be(tcL);
        tzS.Should().Be(tzL);
        for (var i = 0; i < tcS; i++)
        {
            lShort[i].Should().Be(lFull[i]);
            rShort[i].Should().Be(rFull[i]);
        }
    }

    /// <summary>Chroma-DC coefficient layout (four entries, last index 3).</summary>
    [Fact]
    public void CavlcParamCal_chroma_dc_span()
    {
        var c = new short[] { 0, -4, 0, 11 };
        Span<short> level = stackalloc short[16];
        Span<byte> run = stackalloc byte[16];
        H264CavlcResidual.CavlcParamCal(c.AsSpan(), lastIndex: 3, level, run, out var tc, out var tz);
        tc.Should().Be(2);
        tz.Should().Be(2); // zeros at idx 2 between 11 and (-4); zero at idx 0 below (-4)
        level[0].Should().Be(11);
        level[1].Should().Be(-4);
        run[0].Should().Be(1);
        run[1].Should().Be(1);
    }
}
