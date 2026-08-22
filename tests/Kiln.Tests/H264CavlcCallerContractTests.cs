using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>Pins observable CAVLC semantics from <see cref="H264SliceMbWriter"/> / callers.</summary>
public sealed class H264CavlcCallerContractTests
{
    /// <summary>
    /// <see cref="H264SliceMbWriter"/> line ~94: intra 16×16 luma AC uses <see cref="H264ResidualKind.Luma4X4"/>.
    /// <see cref="H264ResidualKind.Luma16x16Ac"/> / <see cref="H264ResidualKind.ChromaAc"/> route equivalently — keep bit-stable.
    /// </summary>
    [Fact]
    public void Packed_ac15_Luma4X4_matches_ChromaAc_and_Luma16x16Ac_bytes()
    {
        var rnd = new Random(0xACC1);
        var coeff = new short[15];
        Span<short> s = stackalloc short[15];

        for (var trial = 0; trial < 400; trial++)
        {
            for (var z = 0; z < 15; z++)
                coeff[z] = (short)(rnd.NextDouble() > 0.55 ? rnd.Next(-9, 9) :
                    rnd.NextDouble() > 0.5 ? -1 : 1);

            coeff.CopyTo(s);

            foreach (var nc in new[] { 0, 2, 4, 7, 8, 11, 16 })
            {
                var a = EncodeNoTrailing(s[..], endIdx: 14, kind: H264ResidualKind.Luma4X4, nc);
                var b = EncodeNoTrailing(s[..], endIdx: 14, kind: H264ResidualKind.Luma16x16Ac, nc);
                var c = EncodeNoTrailing(s[..], endIdx: 14, kind: H264ResidualKind.ChromaAc, nc);
                b.SequenceEqual(a).Should().BeTrue($"{trial}/{nc}");
                c.SequenceEqual(a).Should().BeTrue($"{trial}/{nc}");
            }
        }
    }

    [Fact]
    public void ChromaDc_writer_ignores_nc_argument_completely_on_coeff_token_bundle()
    {
        Span<short> c = stackalloc short[16];
        c[3] = 19;
        c[1] = -4;

        var gold = EncodeNoTrailing(c[..], 3, H264ResidualKind.ChromaDc, nc: int.MinValue);
        foreach (var rogue in new[] { -99, -1, 0, 1, 3, 7, 16, 7777 })
            EncodeNoTrailing(c[..], 3, H264ResidualKind.ChromaDc, rogue).SequenceEqual(gold).Should().BeTrue($"{rogue}");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-777)]
    [InlineData(17)]
    [InlineData(100)]
    public void Luma4x4_nc_mapper_clamps_to_0_through_16_before_EncNc_mapping(int rogue)
    {
        Span<short> w = stackalloc short[16];
        w[15] = -5;
        w[14] = 2;
        w[11] = 6;

        var clampedNc = rogue < 0 ? 0 : rogue > 16 ? 16 : rogue;
        var expect = EncodeNoTrailing(w[..], 15, H264ResidualKind.Luma4X4, clampedNc);
        EncodeNoTrailing(w[..], 15, H264ResidualKind.Luma4X4, rogue).SequenceEqual(expect).Should().BeTrue();

        var decoded = DecodeLuma(expect, rogue);
        decoded.Should().Equal(w.ToArray());
    }

    private static byte[] EncodeNoTrailing(ReadOnlySpan<short> coeff, int endIdx, H264ResidualKind kind, int nc)
    {
        Span<short> ws = stackalloc short[16];
        coeff[..(endIdx + 1)].CopyTo(ws);
        var bs = new H264RbspBitBuffer();
        H264CavlcResidual.WriteBlockResidual(bs, ws, endIdx, kind, nc);
        return bs.ToArray();
    }

    private static short[] DecodeLuma(byte[] bytes, int rogueNcPassedToDecoderClamp)
        => DecodeOnce(bytes, 15, rogueNcPassedToDecoderClamp, isChromaDc: false);

    private static short[] DecodeOnce(byte[] bytes, int endIdx, int nc, bool isChromaDc)
    {
        var br = new H264CavlcSpecDecode.BitReader(bytes);
        return H264CavlcSpecDecode.DecodeBlock(br, endIdx, nc, isChromaDc);
    }
}
