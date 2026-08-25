using FluentAssertions;
using Kiln.Internal.H264;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// SIMD prediction-SSD (<see cref="H264MotionSsd"/>) must match scalar bit-exact on every ISA
/// tier: the Phase-1 P_Skip acceptance gate compares the sum against a threshold, so any
/// difference would change encoding decisions.
/// </summary>
public sealed class H264MotionSsdParityTests
{
    private static int NaiveSsd(
        ReadOnlySpan<byte> a, int strideA,
        ReadOnlySpan<byte> b, int strideB,
        int width, int height)
    {
        var s = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var d = a[y * strideA + x] - b[y * strideB + x];
                s += d * d;
            }
        }

        return s;
    }

    [Fact]
    public void Ssd16x16_scalar_matches_naive()
    {
        var rng = new Random(0x55D0);
        for (var trial = 0; trial < 50; trial++)
        {
            var (a, b) = MotionSadTestHelpers.RandomPair(rng, 24, 18, 16, 16);
            H264MotionSsd.Ssd16x16Scalar(a, 24, b, 16).Should().Be(NaiveSsd(a, 24, b, 16, 16, 16));
        }
    }

    [Fact]
    public void Ssd8x8_scalar_matches_naive()
    {
        var rng = new Random(0x55D1);
        for (var trial = 0; trial < 50; trial++)
        {
            var (a, b) = MotionSadTestHelpers.RandomPair(rng, 12, 10, 8, 8);
            H264MotionSsd.Ssd8x8Scalar(a, 12, b, 8).Should().Be(NaiveSsd(a, 12, b, 8, 8, 8));
        }
    }

    [Fact]
    public void Ssd_advsimd_matches_scalar_when_available()
    {
        if (!AdvSimd.IsSupported)
        {
            return;
        }

        var rng = new Random(0x55D2);
        for (var trial = 0; trial < 50; trial++)
        {
            var (a16, b16) = MotionSadTestHelpers.RandomPair(rng, 24, 18, 16, 16);
            H264MotionSsd.Ssd16x16AdvSimd(a16, 24, b16, 16).Should().Be(H264MotionSsd.Ssd16x16Scalar(a16, 24, b16, 16));

            var (a8, b8) = MotionSadTestHelpers.RandomPair(rng, 12, 10, 8, 8);
            H264MotionSsd.Ssd8x8AdvSimd(a8, 12, b8, 8).Should().Be(H264MotionSsd.Ssd8x8Scalar(a8, 12, b8, 8));
        }
    }

    [Fact]
    public void Ssd_ssse3_matches_scalar_when_available()
    {
        if (!Ssse3.IsSupported)
        {
            return;
        }

        var rng = new Random(0x55D3);
        for (var trial = 0; trial < 50; trial++)
        {
            var (a16, b16) = MotionSadTestHelpers.RandomPair(rng, 24, 18, 16, 16);
            H264MotionSsd.Ssd16x16Ssse3(a16, 24, b16, 16).Should().Be(H264MotionSsd.Ssd16x16Scalar(a16, 24, b16, 16));

            var (a8, b8) = MotionSadTestHelpers.RandomPair(rng, 12, 10, 8, 8);
            H264MotionSsd.Ssd8x8Ssse3(a8, 12, b8, 8).Should().Be(H264MotionSsd.Ssd8x8Scalar(a8, 12, b8, 8));
        }
    }

    [Fact]
    public void Resolved_kernel_set_ssd_matches_scalar_set()
    {
        var best = H264KernelSet.CreateBest();
        var scalar = new ScalarKernelSet();
        var rng = new Random(0x55D4);
        for (var trial = 0; trial < 50; trial++)
        {
            var (a16, b16) = MotionSadTestHelpers.RandomPair(rng, 24, 18, 16, 16);
            best.Ssd16x16(a16, 24, b16, 16).Should().Be(scalar.Ssd16x16(a16, 24, b16, 16));

            var (a8, b8) = MotionSadTestHelpers.RandomPair(rng, 12, 10, 8, 8);
            best.Ssd8x8(a8, 12, b8, 8).Should().Be(scalar.Ssd8x8(a8, 12, b8, 8));
        }
    }

    /// <summary>
    /// Worst-case accumulator stress: all-255 vs all-0 saturates every lane
    /// (256 · 255² for 16×16) without overflowing the 32-bit result.
    /// </summary>
    [Fact]
    public void Ssd_extreme_values_do_not_overflow()
    {
        var a = new byte[24 * 18];
        var b = new byte[16 * 16];
        Array.Fill(a, (byte)255);
        var expected16 = 256 * 255 * 255;
        H264MotionSsd.Ssd16x16Scalar(a, 24, b, 16).Should().Be(expected16);
        var best = H264KernelSet.CreateBest();
        best.Ssd16x16(a, 24, b, 16).Should().Be(expected16);
        best.Ssd8x8(a, 24, b.AsSpan(0, 8 * 8), 8).Should().Be(64 * 255 * 255);
    }
}
