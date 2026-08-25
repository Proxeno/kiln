using FluentAssertions;
using Kiln.Internal.H264;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// SIMD 16×16 MB variance (<see cref="H264VarianceFastPath.VarianceMb16x16Scalar"/> and its NEON /
/// SSE2-tier variants) must match scalar bit-exact on every ISA tier: adaptive-QP offsets and the
/// inter search-range selection both threshold-compare the value, so any difference would change
/// encoding decisions and therefore the bitstream.
/// </summary>
public sealed class H264VarianceMb16x16ParityTests
{
    private static int NaiveVariance(ReadOnlySpan<byte> src, int stride)
    {
        long sum = 0;
        long sumsq = 0;
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                long v = src[y * stride + x];
                sum += v;
                sumsq += v * v;
            }
        }

        return (int)((256 * sumsq - sum * sum) / (256 * 256));
    }

    private static byte[] RandomBlock(Random rng, int stride, int rows)
    {
        var data = new byte[stride * rows];
        rng.NextBytes(data);
        return data;
    }

    [Fact]
    public void Variance_scalar_matches_naive()
    {
        var rng = new Random(0x7A11);
        foreach (var stride in new[] { 16, 24, 640 })
        {
            for (var trial = 0; trial < 50; trial++)
            {
                var a = RandomBlock(rng, stride, 16);
                H264VarianceFastPath.VarianceMb16x16Scalar(a, stride).Should().Be(NaiveVariance(a, stride));
            }
        }
    }

    [Fact]
    public void Variance_advsimd_matches_scalar_when_available()
    {
        if (!AdvSimd.IsSupported)
        {
            return;
        }

        var rng = new Random(0x7A12);
        foreach (var stride in new[] { 16, 24, 640 })
        {
            for (var trial = 0; trial < 50; trial++)
            {
                var a = RandomBlock(rng, stride, 16);
                H264VarianceFastPath.VarianceMb16x16AdvSimd(a, stride)
                    .Should().Be(H264VarianceFastPath.VarianceMb16x16Scalar(a, stride));
            }
        }
    }

    [Fact]
    public void Variance_ssse3_matches_scalar_when_available()
    {
        if (!Ssse3.IsSupported)
        {
            return;
        }

        var rng = new Random(0x7A13);
        foreach (var stride in new[] { 16, 24, 640 })
        {
            for (var trial = 0; trial < 50; trial++)
            {
                var a = RandomBlock(rng, stride, 16);
                H264VarianceFastPath.VarianceMb16x16Ssse3(a, stride)
                    .Should().Be(H264VarianceFastPath.VarianceMb16x16Scalar(a, stride));
            }
        }
    }

    [Fact]
    public void Resolved_kernel_set_variance_matches_scalar_set()
    {
        var best = H264KernelSet.CreateBest();
        var scalar = new ScalarKernelSet();
        var rng = new Random(0x7A14);
        for (var trial = 0; trial < 50; trial++)
        {
            var a = RandomBlock(rng, 24, 16);
            best.VarianceMb16x16(a, 24).Should().Be(scalar.VarianceMb16x16(a, 24));
        }
    }

    /// <summary>
    /// Accumulator stress: all-255 saturates Σx (256 · 255 = 65 280, the u16 ADDV / PSADBW ceiling)
    /// and Σx² (256 · 255²) while the variance itself is 0; an alternating 0/255 checkerboard is the
    /// maximum-variance input (255²/4 · 4 = 16 256 after the /256² scale).
    /// </summary>
    [Fact]
    public void Variance_extreme_values_are_exact()
    {
        var flat = new byte[24 * 16];
        Array.Fill(flat, (byte)255);
        H264VarianceFastPath.VarianceMb16x16Scalar(flat, 24).Should().Be(0);

        var checker = new byte[24 * 16];
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                checker[y * 24 + x] = (byte)(((x + y) & 1) == 0 ? 255 : 0);
            }
        }

        var expected = NaiveVariance(checker, 24);
        H264VarianceFastPath.VarianceMb16x16Scalar(checker, 24).Should().Be(expected);
        if (AdvSimd.IsSupported)
        {
            H264VarianceFastPath.VarianceMb16x16AdvSimd(flat, 24).Should().Be(0);
            H264VarianceFastPath.VarianceMb16x16AdvSimd(checker, 24).Should().Be(expected);
        }

        if (Ssse3.IsSupported)
        {
            H264VarianceFastPath.VarianceMb16x16Ssse3(flat, 24).Should().Be(0);
            H264VarianceFastPath.VarianceMb16x16Ssse3(checker, 24).Should().Be(expected);
        }
    }
}
