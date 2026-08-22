using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Parity gate for <see cref="H264ChromaSadSimd.SadU8x8Pair"/>: every (srcU, srcV, predU, predV)
/// tuple must produce the same scalar SAD as the per-pixel reduction in
/// <c>H264BaselineSliceEncoder.ChooseChromaIntraMode</c>. Skips on non-SIMD hosts.
/// </summary>
public sealed class H264ChromaSadSimdTests
{
    [Theory]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(2024)]
    public void SadU8x8Pair_simd_matches_scalar(int seed)
    {
        if (!H264ChromaSadSimd.IsSupported)
        {
            return;
        }

        var rng = new Random(seed);
        for (var trial = 0; trial < 64; trial++)
        {
            var srcU = new byte[64];
            var srcV = new byte[64];
            var predU = new byte[64];
            var predV = new byte[64];
            rng.NextBytes(srcU);
            rng.NextBytes(srcV);
            rng.NextBytes(predU);
            rng.NextBytes(predV);

            var simd = H264ChromaSadSimd.SadU8x8Pair(srcU, srcV, predU, predV);
            var scalar = ScalarSad(srcU, srcV, predU, predV);
            simd.Should().Be(scalar, "trial {0} must be lane-by-lane equal", trial);
        }
    }

    [Fact]
    public void SadU8x8Pair_returns_zero_when_inputs_match()
    {
        if (!H264ChromaSadSimd.IsSupported)
        {
            return;
        }

        var u = new byte[64];
        var v = new byte[64];
        new Random(1).NextBytes(u);
        new Random(2).NextBytes(v);
        H264ChromaSadSimd.SadU8x8Pair(u, v, u, v).Should().Be(0);
    }

    [Fact]
    public void SadU8x8Pair_returns_max_when_inputs_complement()
    {
        if (!H264ChromaSadSimd.IsSupported)
        {
            return;
        }

        var srcU = new byte[64];
        var srcV = new byte[64];
        var predU = new byte[64];
        var predV = new byte[64];
        srcU.AsSpan().Fill(255);
        srcV.AsSpan().Fill(255);
        predU.AsSpan().Fill(0);
        predV.AsSpan().Fill(0);
        // 128 lanes × 255 = 32640.
        H264ChromaSadSimd.SadU8x8Pair(srcU, srcV, predU, predV).Should().Be(32640);
    }

    private static int ScalarSad(
        ReadOnlySpan<byte> srcU,
        ReadOnlySpan<byte> srcV,
        ReadOnlySpan<byte> predU,
        ReadOnlySpan<byte> predV)
    {
        var sad = 0;
        for (var y = 0; y < 8; y++)
        {
            var off = y * 8;
            for (var x = 0; x < 8; x++)
            {
                sad += Math.Abs(srcU[off + x] - predU[off + x]);
                sad += Math.Abs(srcV[off + x] - predV[off + x]);
            }
        }

        return sad;
    }
}
