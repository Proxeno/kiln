using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

public sealed class H264MotionSatdParityTests
{
    [Fact]
    public void SatdMany4x4_scalar_matches_individual_satd()
    {
        var rng = new Random(1234);
        var src = new byte[16];
        var preds = new byte[9 * 16];
        rng.NextBytes(src);
        rng.NextBytes(preds);
        Span<int> satds = stackalloc int[9];

        H264MotionSatd.SatdMany4x4Scalar(src, preds, satds, 9);

        for (var i = 0; i < 9; i++)
        {
            satds[i].Should().Be(H264Hadamard4x4.Satd(src, preds.AsSpan(i * 16, 16)));
        }
    }

    [Fact]
    public void SatdMany4x4_simd_or_best_kernel_matches_scalar()
    {
        var rng = new Random(5678);
        var kernels = H264KernelSet.CreateBest();
        var scalar = new ScalarKernelSet();
        var src = new byte[16];
        var preds = new byte[9 * 16];
        Span<int> scalarSatds = stackalloc int[9];
        Span<int> bestSatds = stackalloc int[9];

        for (var trial = 0; trial < 100; trial++)
        {
            rng.NextBytes(src);
            rng.NextBytes(preds);

            scalar.SatdMany4x4(src, preds, scalarSatds, 9);
            kernels.SatdMany4x4(src, preds, bestSatds, 9);

            for (var i = 0; i < 9; i++)
            {
                bestSatds[i].Should().Be(scalarSatds[i], $"trial={trial} i={i}");
            }
        }
    }

    [Fact]
    public void MotionSatd16x16_is_non_negative_on_random_blocks()
    {
        var rng = new Random(42);
        var a = new byte[256];
        var b = new byte[256];
        rng.NextBytes(a);
        rng.NextBytes(b);

        var s = H264MotionSatd.Satd16x16(a, 16, b, 16);
        s.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void MotionSatd_block_shapes_simd_matches_scalar_with_strides()
    {
        if (!H264MotionSatd.IsSupported)
        {
            return;
        }

        var rng = new Random(0xA57D);
        const int strideA = 29;
        const int strideB = 31;
        var a = new byte[strideA * 24];
        var b = new byte[strideB * 24];

        for (var trial = 0; trial < 100; trial++)
        {
            rng.NextBytes(a);
            rng.NextBytes(b);

            H264MotionSatd.Satd16x16Simd(a, strideA, b, strideB)
                .Should().Be(H264MotionSatd.Satd16x16Scalar(a, strideA, b, strideB), $"16x16 trial={trial}");
            H264MotionSatd.Satd16x8Simd(a, strideA, b, strideB)
                .Should().Be(H264MotionSatd.Satd16x8Scalar(a, strideA, b, strideB), $"16x8 trial={trial}");
            H264MotionSatd.Satd8x16Simd(a, strideA, b, strideB)
                .Should().Be(H264MotionSatd.Satd8x16Scalar(a, strideA, b, strideB), $"8x16 trial={trial}");
            H264MotionSatd.Satd8x8Simd(a, strideA, b, strideB)
                .Should().Be(H264MotionSatd.Satd8x8Scalar(a, strideA, b, strideB), $"8x8 trial={trial}");
        }
    }

    [Fact]
    public void MotionSatd_block_shapes_match_4x4_reference_with_strides()
    {
        var rng = new Random(0x5A7D);
        const int strideA = 29;
        const int strideB = 31;
        var a = new byte[strideA * 24];
        var b = new byte[strideB * 24];
        rng.NextBytes(a);
        rng.NextBytes(b);

        Verify("16x16",
            H264MotionSatd.Satd16x16(a, strideA, b, strideB),
            ReferenceSatdNxM(a, strideA, b, strideB, blocksX: 4, blocksY: 4));
        Verify("16x8",
            H264MotionSatd.Satd16x8(a, strideA, b, strideB),
            ReferenceSatdNxM(a, strideA, b, strideB, blocksX: 4, blocksY: 2));
        Verify("8x16",
            H264MotionSatd.Satd8x16(a, strideA, b, strideB),
            ReferenceSatdNxM(a, strideA, b, strideB, blocksX: 2, blocksY: 4));
        Verify("8x8",
            H264MotionSatd.Satd8x8(a, strideA, b, strideB),
            ReferenceSatdNxM(a, strideA, b, strideB, blocksX: 2, blocksY: 2));
    }

    [Fact]
    public void MotionSatd_block_shapes_are_bounded_by_half_sad()
    {
        var rng = new Random(0x51AD);
        const int strideA = 29;
        const int strideB = 31;
        var a = new byte[strideA * 24];
        var b = new byte[strideB * 24];

        for (var trial = 0; trial < 200; trial++)
        {
            rng.NextBytes(a);
            rng.NextBytes(b);

            VerifyHalfSadBound(
                H264MotionSatd.Satd16x16(a, strideA, b, strideB),
                H264MotionSad.Sad16x16Scalar(a, strideA, b, strideB),
                "16x16");
            VerifyHalfSadBound(
                H264MotionSatd.Satd16x8(a, strideA, b, strideB),
                H264MotionSad.Sad16x8Scalar(a, strideA, b, strideB),
                "16x8");
            VerifyHalfSadBound(
                H264MotionSatd.Satd8x16(a, strideA, b, strideB),
                H264MotionSad.Sad8x16Scalar(a, strideA, b, strideB),
                "8x16");
            VerifyHalfSadBound(
                H264MotionSatd.Satd8x8(a, strideA, b, strideB),
                H264MotionSad.Sad8x8Scalar(a, strideA, b, strideB),
                "8x8");
        }
    }

    [Fact]
    public void MotionSatd_transformed_4x4_matches_strided_satd()
    {
        var rng = new Random(0x7A4D);
        const int strideA = 27;
        const int strideB = 31;
        var a = new byte[strideA * 24];
        var b = new byte[strideB * 24];
        Span<short> coeffA = stackalloc short[H264MotionSatd.Transform4x4CoefficientCount];
        Span<short> coeffB = stackalloc short[H264MotionSatd.Transform4x4CoefficientCount];

        for (var trial = 0; trial < 200; trial++)
        {
            rng.NextBytes(a);
            rng.NextBytes(b);
            var ax = rng.Next(0, 20);
            var ay = rng.Next(0, 20);
            var bx = rng.Next(0, 24);
            var by = rng.Next(0, 20);

            H264MotionSatd.Transform4x4Strided(a, strideA, ax, ay, coeffA);
            H264MotionSatd.Transform4x4Strided(b, strideB, bx, by, coeffB);

            var transformed = H264MotionSatd.Satd4x4FromTransformed(coeffA, coeffB);
            var direct = H264MotionSatd.Satd4x4Strided(a, strideA, ax, ay, b, strideB, bx, by);
            transformed.Should().Be(direct, $"trial={trial}");
        }
    }

    [Fact]
    public void SearchMb16x16_satd_path_returns_valid_score_on_matching_reference_window()
    {
        const int rw = 64;
        const int rh = 64;
        var stride = rw;
        var refPic = new byte[stride * rh];
        for (var y = 0; y < rh; y++)
        for (var x = 0; x < rw; x++)
            refPic[y * stride + x] = (byte)((x + y) & 0xFF);

        const int mbX = 8;
        const int mbY = 8;
        var current = new byte[16 * 16];
        for (var row = 0; row < 16; row++)
            refPic.AsSpan((mbY + row) * stride + mbX, 16).CopyTo(current.AsSpan(row * 16, 16));

        var r = H264MotionEstimator.SearchMb16x16(
            current, 16,
            refPic, stride,
            mbX, mbY,
            default,
            searchRange: 8,
            useMotionSatd: true,
            kernels: H264MeTestHelpers.Kernels);

        r.BestSad.Should().BeGreaterThanOrEqualTo(0);
        r.BestSad.Should().BeLessThan(int.MaxValue);
    }

    private static int ReferenceSatdNxM(
        ReadOnlySpan<byte> a, int strideA,
        ReadOnlySpan<byte> b, int strideB,
        int blocksX, int blocksY)
    {
        Span<byte> blockA = stackalloc byte[16];
        Span<byte> blockB = stackalloc byte[16];
        var sum = 0;

        for (var by = 0; by < blocksY; by++)
        {
            for (var bx = 0; bx < blocksX; bx++)
            {
                for (var y = 0; y < 4; y++)
                {
                    var srcA = ((by * 4) + y) * strideA + (bx * 4);
                    var srcB = ((by * 4) + y) * strideB + (bx * 4);
                    a.Slice(srcA, 4).CopyTo(blockA.Slice(y * 4, 4));
                    b.Slice(srcB, 4).CopyTo(blockB.Slice(y * 4, 4));
                }

                sum += H264Hadamard4x4.Satd(blockA, blockB);
            }
        }

        return sum;
    }

    private static void Verify(string shape, int actual, int expected)
    {
        actual.Should().Be(expected, $"{shape} SATD must equal the sum of exact 4x4 SATD blocks.");
    }

    private static void VerifyHalfSadBound(int satd, int sad, string shape)
    {
        satd.Should().BeGreaterThanOrEqualTo((sad + 1) >> 1, $"{shape} SATD must safely bound half SAD.");
    }
}
