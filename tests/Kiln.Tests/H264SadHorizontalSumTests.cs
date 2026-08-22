using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Parity for 16-byte SAD horizontal reduction in <see cref="H264Intra4X4Simd.SadU8x16"/> (SSSE3
/// <c>psadbw</c> on x64, NEON abs-diff + widen on Arm64). Covers max-diff and identical-buffer edges.
/// </summary>
public sealed class H264SadHorizontalSumTests
{
    private static int NaiveSad(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var s = 0;
        for (var i = 0; i < 16; i++)
        {
            s += Math.Abs(a[i] - b[i]);
        }

        return s;
    }

    public static IEnumerable<object[]> Seeds()
    {
        for (var seed = 1; seed <= 10; seed++)
        {
            yield return [seed];
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void SadU8x16_matches_naive_for_rng_seeded_buffers(int seed)
    {
        if (!H264Intra4X4Simd.IsSupported)
        {
            return;
        }

        EncoderSimdTestFacts.RequiresX64EncoderSimd();

        var rng = new Random(seed);
        var a = new byte[16];
        var b = new byte[16];
        for (var trial = 0; trial < 10; trial++)
        {
            rng.NextBytes(a);
            rng.NextBytes(b);
            var expected = NaiveSad(a, b);
            var actual = H264Intra4X4Simd.SadU8x16(a, b);
            actual.Should().Be(expected, $"seed={seed} trial={trial}");
        }
    }

    [Fact]
    public void SadU8x16_zero_for_all_zero_buffers()
    {
        if (!H264Intra4X4Simd.IsSupported)
        {
            return;
        }

        var a = new byte[16];
        var b = new byte[16];
        H264Intra4X4Simd.SadU8x16(a, b).Should().Be(0);
    }

    [Fact]
    public void SadU8x16_zero_for_all_max_buffers()
    {
        if (!H264Intra4X4Simd.IsSupported)
        {
            return;
        }

        var a = new byte[16];
        var b = new byte[16];
        Array.Fill(a, (byte)255);
        Array.Fill(b, (byte)255);
        H264Intra4X4Simd.SadU8x16(a, b).Should().Be(0);
    }

    [Fact]
    public void SadU8x16_zero_for_identical_buffers()
    {
        if (!H264Intra4X4Simd.IsSupported)
        {
            return;
        }

        var rng = new Random(12345);
        var a = new byte[16];
        rng.NextBytes(a);
        var b = (byte[])a.Clone();
        H264Intra4X4Simd.SadU8x16(a, b).Should().Be(0);
    }

    [Fact]
    public void SadU8x16_alternating_bit_pattern()
    {
        if (!H264Intra4X4Simd.IsSupported)
        {
            return;
        }

        var a = new byte[16];
        var b = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            a[i] = (i & 1) == 0 ? (byte)0xAA : (byte)0x55;
            b[i] = (i & 1) == 0 ? (byte)0x55 : (byte)0xAA;
        }

        H264Intra4X4Simd.SadU8x16(a, b).Should().Be(NaiveSad(a, b));
    }

    [Fact]
    public void SadU8x16_single_byte_differs()
    {
        if (!H264Intra4X4Simd.IsSupported)
        {
            return;
        }

        for (var pos = 0; pos < 16; pos++)
        {
            var a = new byte[16];
            var b = new byte[16];
            Array.Fill(a, (byte)100);
            Array.Fill(b, (byte)100);
            b[pos] = 200;
            H264Intra4X4Simd.SadU8x16(a, b).Should().Be(100, $"single byte differs at pos={pos}");
        }
    }

    /// <summary>
    /// Upper-bound stress: every byte differs by 255 (4080 total). Horizontal reduction must widen
    /// before accumulating; a naive byte-domain add-across would saturate or wrap.
    /// </summary>
    [Fact]
    public void SadU8x16_max_difference_sums_to_4080()
    {
        if (!H264Intra4X4Simd.IsSupported)
        {
            return;
        }

        EncoderSimdTestFacts.RequiresX64EncoderSimd();

        var a = new byte[16];
        var b = new byte[16];
        Array.Fill(a, (byte)0);
        Array.Fill(b, (byte)255);
        H264Intra4X4Simd.SadU8x16(a, b).Should().Be(4080);
        H264Intra4X4Simd.SadU8x16(b, a).Should().Be(4080);
    }
}
