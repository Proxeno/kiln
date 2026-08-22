using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

public sealed class H264CavlcChromaDcBitCountTests
{
    [Fact]
    public void CountChromaDcResidualBits_matches_WriteBlockResidual_bit_length()
    {
        var rnd = new Random(42);
        Span<short> c = stackalloc short[4];
        Span<short> l = stackalloc short[16];
        Span<byte> r = stackalloc byte[16];
        for (var t = 0; t < 400; t++)
        {
            for (var i = 0; i < 4; i++)
            {
                c[i] = (short)rnd.Next(-400, 400);
            }

            AssertCountMatchesWritten(c, l, r, $"trial={t}");
        }
    }

    public static IEnumerable<object[]> ChromaDcEdgeCases =>
    [
        [new short[] { 0, 0, 0, 0 }],
        [new short[] { 7, 0, 0, 0 }],
        [new short[] { 8, -1, 0, 0 }],
        [new short[] { 9, -1, 3, -2 }],
        [new short[] { 17, -2, -3, -4 }],
        [new short[] { 120, -80, 15, -15 }],
        [new short[] { 1, 1, 1, 1 }],
        [new short[] { -99, -5, -1, -1 }],
        [new short[] { -2, -2, -2, -2 }],
        [new short[] { -3, -2, -1, 1 }],
    ];

    [Theory]
    [MemberData(nameof(ChromaDcEdgeCases))]
    public void ChromaDc_count_matches_written_on_edge_vectors(short[] vec)
    {
        Span<short> c = stackalloc short[4];
        vec.AsSpan().CopyTo(c);
        Span<short> l = stackalloc short[16];
        Span<byte> r = stackalloc byte[16];
        AssertCountMatchesWritten(c, l, r, $"vec=[{vec[0]},{vec[1]},{vec[2]},{vec[3]}]");
    }

    [Theory]
    [MemberData(nameof(ChromaDcEdgeCases))]
    public void ChromaDc_roundtrip_spec_decode(short[] vec)
    {
        var rndNc = Random.Shared.Next(0, 100);
        Span<short> c = stackalloc short[4];
        vec.AsSpan().CopyTo(c);
        Span<short> work = stackalloc short[16];
        c.CopyTo(work);
        var bytes = H264CavlcSpecDecode.EncodeBlock(work, 3, H264ResidualKind.ChromaDc, rndNc);
        var br = new H264CavlcSpecDecode.BitReader(bytes);
        var decoded = H264CavlcSpecDecode.DecodeBlock(br, 3, rndNc, isChromaDc: true);
        decoded.Should().Equal(vec, $"ChromDc coef round-trip inconsistent (nc clamp path). vec=[..]");
    }

    [Fact]
    public void ChromaDc_coeff_token_bits_are_independent_of_nc_parameter()
    {
        short[] vec = [-30, -7, -3, -2];
        var bs = new H264RbspBitBuffer();
        Span<short> c = stackalloc short[16];
        vec.AsSpan().CopyTo(c);
        bs.Reset();
        H264CavlcResidual.WriteBlockResidual(bs, c, 3, H264ResidualKind.ChromaDc, 0);
        var a = bs.ToArray();
        bs.Reset();
        c.Clear();
        vec.AsSpan().CopyTo(c);
        H264CavlcResidual.WriteBlockResidual(bs, c, 3, H264ResidualKind.ChromaDc, -17);
        var b = bs.ToArray();
        a.Should().Equal(b);
        bs.Reset();
        c.Clear();
        vec.AsSpan().CopyTo(c);
        H264CavlcResidual.WriteBlockResidual(bs, c, 3, H264ResidualKind.ChromaDc, 777);
        var d = bs.ToArray();
        a.Should().Equal(d);
    }

    private static void AssertCountMatchesWritten(
        Span<short> coeff4,
        Span<short> l,
        Span<byte> r,
        string because)
    {
        var bs = new H264RbspBitBuffer(512);
        var before = bs.BitLength;
        H264CavlcResidual.WriteBlockResidual(bs, coeff4, 3, H264ResidualKind.ChromaDc, 0);
        var written = bs.BitLength - before;
        var counted = H264CavlcResidual.CountChromaDcResidualBits(coeff4, l, r);
        counted.Should().Be(written, because);
    }
}
