using FluentAssertions;
using Kiln.Internal.H264;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Acceptance test for Junior-G-sad: every block-size SAD must agree with a naive
/// <c>sum |a[i] - b[i]|</c> reference, and the SIMD dispatch must agree with the scalar path bit-exact
/// when <see cref="H264MotionSad.IsSupported"/> is true. Strides for <c>a</c> and <c>b</c> may differ
/// (motion search slides one block over a reference patch with its own stride).
/// </summary>
public sealed class H264MultiBlockSadTests
{
    [Theory]
    // strideA must be >= 16 for a 16-wide read; the original [InlineData(11, 16, 13, 17)] row had
    // strideA=11 < width=16 so RandomPair sized `a` to 11 * 16 = 176 bytes and the SAD's `a[15*11+15]`
    // = `a[180]` overran the buffer. Bumping both strides above the block width fixes the fixture
    // without weakening the assertion (the test still varies stride per row).
    [InlineData(18, 18, 17, 17)]
    [InlineData(16, 16, 16, 16)]
    [InlineData(33, 22, 24, 20)]
    public void Sad16x16_scalar_matches_naive_for_random_strides(int strideA, int rowsA, int strideB, int rowsB)
    {
        var rng = new Random(0xA5A5);
        for (var trial = 0; trial < 30; trial++)
        {
            var (a, b) = MotionSadTestHelpers.RandomPair(rng, strideA, rowsA, strideB, rowsB);
            var got = H264MotionSad.Sad16x16Scalar(a, strideA, b, strideB);
            var want = MotionSadTestHelpers.NaiveSad(a, strideA, b, strideB, 16, 16);
            got.Should().Be(want);
        }
    }

    [Theory]
    [InlineData(20, 12, 19, 14)]
    public void Sad16x8_scalar_matches_naive(int strideA, int rowsA, int strideB, int rowsB)
    {
        var rng = new Random(0xA5A6);
        for (var trial = 0; trial < 30; trial++)
        {
            var (a, b) = MotionSadTestHelpers.RandomPair(rng, strideA, rowsA, strideB, rowsB);
            var got = H264MotionSad.Sad16x8Scalar(a, strideA, b, strideB);
            var want = MotionSadTestHelpers.NaiveSad(a, strideA, b, strideB, 16, 8);
            got.Should().Be(want);
        }
    }

    [Theory]
    [InlineData(12, 20, 13, 22)]
    public void Sad8x16_scalar_matches_naive(int strideA, int rowsA, int strideB, int rowsB)
    {
        var rng = new Random(0xA5A7);
        for (var trial = 0; trial < 30; trial++)
        {
            var (a, b) = MotionSadTestHelpers.RandomPair(rng, strideA, rowsA, strideB, rowsB);
            var got = H264MotionSad.Sad8x16Scalar(a, strideA, b, strideB);
            var want = MotionSadTestHelpers.NaiveSad(a, strideA, b, strideB, 8, 16);
            got.Should().Be(want);
        }
    }

    [Theory]
    [InlineData(11, 12, 9, 10)]
    [InlineData(8, 8, 8, 8)]
    public void Sad8x8_scalar_matches_naive(int strideA, int rowsA, int strideB, int rowsB)
    {
        var rng = new Random(0xA5A8);
        for (var trial = 0; trial < 30; trial++)
        {
            var (a, b) = MotionSadTestHelpers.RandomPair(rng, strideA, rowsA, strideB, rowsB);
            var got = H264MotionSad.Sad8x8Scalar(a, strideA, b, strideB);
            var want = MotionSadTestHelpers.NaiveSad(a, strideA, b, strideB, 8, 8);
            got.Should().Be(want);
        }
    }

    [Fact]
    public void Sad16x16_dispatch_matches_scalar()
    {
        if (!H264MotionSad.IsSupported)
        {
            return;
        }

        EncoderSimdTestFacts.RequiresX64EncoderSimd();

        using (new H264IntrinsicsPreference.Scope(preferIntrinsics: true))
        {
            var rng = new Random(0xB001);
            for (var trial = 0; trial < 50; trial++)
            {
                var (a, b) = MotionSadTestHelpers.RandomPair(rng, 24, 18, 22, 20);
                var dispatched = H264MotionSad.Sad16x16(a, 24, b, 22);
                var scalar = H264MotionSad.Sad16x16Scalar(a, 24, b, 22);
                dispatched.Should().Be(scalar);
            }
        }
    }

    [Fact]
    public void Sad16x8_dispatch_matches_scalar()
    {
        if (!H264MotionSad.IsSupported)
        {
            return;
        }

        EncoderSimdTestFacts.RequiresX64EncoderSimd();

        using (new H264IntrinsicsPreference.Scope(preferIntrinsics: true))
        {
            var rng = new Random(0xB002);
            for (var trial = 0; trial < 50; trial++)
            {
                var (a, b) = MotionSadTestHelpers.RandomPair(rng, 24, 12, 22, 14);
                var dispatched = H264MotionSad.Sad16x8(a, 24, b, 22);
                var scalar = H264MotionSad.Sad16x8Scalar(a, 24, b, 22);
                dispatched.Should().Be(scalar);
            }
        }
    }

    [Fact]
    public void Sad8x16_dispatch_matches_scalar()
    {
        if (!H264MotionSad.IsSupported)
        {
            return;
        }

        EncoderSimdTestFacts.RequiresX64EncoderSimd();

        using (new H264IntrinsicsPreference.Scope(preferIntrinsics: true))
        {
            var rng = new Random(0xB003);
            for (var trial = 0; trial < 50; trial++)
            {
                var (a, b) = MotionSadTestHelpers.RandomPair(rng, 16, 22, 14, 20);
                var dispatched = H264MotionSad.Sad8x16(a, 16, b, 14);
                var scalar = H264MotionSad.Sad8x16Scalar(a, 16, b, 14);
                dispatched.Should().Be(scalar);
            }
        }
    }

    [Fact]
    public void Sad8x8_dispatch_matches_scalar()
    {
        if (!H264MotionSad.IsSupported)
        {
            return;
        }

        EncoderSimdTestFacts.RequiresX64EncoderSimd();

        using (new H264IntrinsicsPreference.Scope(preferIntrinsics: true))
        {
            var rng = new Random(0xB004);
            for (var trial = 0; trial < 50; trial++)
            {
                var (a, b) = MotionSadTestHelpers.RandomPair(rng, 16, 12, 14, 10);
                var dispatched = H264MotionSad.Sad8x8(a, 16, b, 14);
                var scalar = H264MotionSad.Sad8x8Scalar(a, 16, b, 14);
                dispatched.Should().Be(scalar);
            }
        }
    }

    /// <summary>Boundary case: identical buffers ⇒ SAD = 0 across all sizes (catches trivial off-by-one stride bugs).</summary>
    [Fact]
    public void Sad_is_zero_for_identical_inputs()
    {
        var rng = new Random(0xC001);
        var buf = new byte[16 * 16];
        rng.NextBytes(buf);

        H264MotionSad.Sad16x16Scalar(buf, 16, buf, 16).Should().Be(0);
        H264MotionSad.Sad16x8Scalar(buf, 16, buf, 16).Should().Be(0);
        H264MotionSad.Sad8x16Scalar(buf, 16, buf, 16).Should().Be(0);
        H264MotionSad.Sad8x8Scalar(buf, 16, buf, 16).Should().Be(0);
    }

    /// <summary>Boundary case: max difference ⇒ SAD = 255·N for N samples; checks no overflow / sign bug.</summary>
    [Fact]
    public void Sad_saturates_for_max_contrast_inputs()
    {
        var a = new byte[32 * 32];
        var b = new byte[32 * 32];
        Array.Fill(b, (byte)255);

        H264MotionSad.Sad16x16Scalar(a, 32, b, 32).Should().Be(255 * 16 * 16);
        H264MotionSad.Sad16x8Scalar(a, 32, b, 32).Should().Be(255 * 16 * 8);
        H264MotionSad.Sad8x16Scalar(a, 32, b, 32).Should().Be(255 * 8 * 16);
        H264MotionSad.Sad8x8Scalar(a, 32, b, 32).Should().Be(255 * 8 * 8);
    }

    /// <summary>Max-contrast through SIMD dispatch (16-wide rows may use AVX2 on x64).</summary>
    [Fact]
    public void Sad_dispatch_saturates_for_max_contrast_inputs()
    {
        if (!H264MotionSad.IsSupported)
        {
            return;
        }

        EncoderSimdTestFacts.RequiresX64EncoderSimd();

        var a = new byte[32 * 32];
        var b = new byte[32 * 32];
        Array.Fill(b, (byte)255);

        using (new H264IntrinsicsPreference.Scope(preferIntrinsics: true))
        {
            H264MotionSad.Sad16x16(a, 32, b, 32).Should().Be(255 * 16 * 16);
            H264MotionSad.Sad16x8(a, 32, b, 32).Should().Be(255 * 16 * 8);
            H264MotionSad.Sad8x16(a, 16, b, 16).Should().Be(255 * 8 * 16);
            H264MotionSad.Sad8x8(a, 16, b, 16).Should().Be(255 * 8 * 8);
        }
    }

    [Theory]
    [InlineData(18, 18, 17, 17)]
    [InlineData(33, 22, 24, 20)]
    public void Sad16x16_dispatch_matches_scalar_under_wide_strides(int strideA, int rowsA, int strideB, int rowsB)
    {
        if (!H264MotionSad.IsSupported)
        {
            return;
        }

        using (new H264IntrinsicsPreference.Scope(preferIntrinsics: true))
        {
            var rng = new Random(0xD16D);
            for (var trial = 0; trial < 30; trial++)
            {
                var (a, b) = MotionSadTestHelpers.RandomPair(rng, strideA, rowsA, strideB, rowsB);
                H264MotionSad.Sad16x16(a, strideA, b, strideB)
                    .Should()
                    .Be(H264MotionSad.Sad16x16Scalar(a, strideA, b, strideB));
            }
        }
    }

    // ── NEON (AdvSimd) kernel parity ────────────────────────────────────────────────────────────

    [Theory]
    // Strides that are not multiples of 16 exercise the unaligned row loads.
    [InlineData(21, 16, 19, 16)]
    [InlineData(24, 16, 17, 16)]
    [InlineData(16, 16, 16, 16)]
    public void Sad_advsimd_matches_scalar_when_available(int strideA, int rowsA, int strideB, int rowsB)
    {
        if (!AdvSimd.IsSupported)
        {
            return;
        }

        var rng = new Random(0x4E01);
        for (var trial = 0; trial < 50; trial++)
        {
            var (a, b) = MotionSadTestHelpers.RandomPair(rng, strideA, rowsA, strideB, rowsB);
            H264MotionSad.Sad16x16AdvSimd(a, strideA, b, strideB)
                .Should().Be(MotionSadTestHelpers.NaiveSad(a, strideA, b, strideB, 16, 16));
            H264MotionSad.Sad16x8AdvSimd(a, strideA, b, strideB)
                .Should().Be(MotionSadTestHelpers.NaiveSad(a, strideA, b, strideB, 16, 8));
            H264MotionSad.Sad8x16AdvSimd(a, strideA, b, strideB)
                .Should().Be(MotionSadTestHelpers.NaiveSad(a, strideA, b, strideB, 8, 16));
            H264MotionSad.Sad8x8AdvSimd(a, strideA, b, strideB)
                .Should().Be(MotionSadTestHelpers.NaiveSad(a, strideA, b, strideB, 8, 8));
        }
    }

    /// <summary>
    /// Extreme pixel patterns through the NEON path: identical buffers (SAD 0), all-0 vs all-255
    /// (SAD = 255 · N, the worst case for the 16-bit accumulator) and a checkerboard difference
    /// capped at the 8-bit sample range.
    /// </summary>
    [Fact]
    public void Sad_advsimd_extreme_patterns()
    {
        if (!AdvSimd.IsSupported)
        {
            return;
        }

        const int stride = 24;
        var zeros = new byte[stride * 16];
        var ones = new byte[stride * 16];
        Array.Fill(ones, (byte)255);
        var checker = new byte[stride * 16];
        for (var i = 0; i < checker.Length; i++)
        {
            checker[i] = (byte)((i & 1) == 0 ? 0 : 255);
        }

        H264MotionSad.Sad16x16AdvSimd(zeros, stride, zeros, stride).Should().Be(0);
        H264MotionSad.Sad8x8AdvSimd(zeros, stride, zeros, stride).Should().Be(0);
        H264MotionSad.Sad16x16AdvSimd(zeros, stride, ones, stride).Should().Be(255 * 16 * 16);
        H264MotionSad.Sad16x8AdvSimd(zeros, stride, ones, stride).Should().Be(255 * 16 * 8);
        H264MotionSad.Sad8x16AdvSimd(zeros, stride, ones, stride).Should().Be(255 * 8 * 16);
        H264MotionSad.Sad8x8AdvSimd(zeros, stride, ones, stride).Should().Be(255 * 8 * 8);
        H264MotionSad.Sad16x16AdvSimd(zeros, stride, checker, stride)
            .Should().Be(MotionSadTestHelpers.NaiveSad(zeros, stride, checker, stride, 16, 16));
        H264MotionSad.Sad8x8AdvSimd(zeros, stride, checker, stride)
            .Should().Be(MotionSadTestHelpers.NaiveSad(zeros, stride, checker, stride, 8, 8));
    }

    // ── Four-candidate kernels ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(21, 16, 19, 16)]
    [InlineData(16, 16, 16, 16)]
    public void Sad16x16x4_matches_four_scalar_calls(int strideA, int rowsA, int strideB, int rowsB)
    {
        var rng = new Random(0x4F01);
        Span<int> sads = stackalloc int[4];
        Span<int> simd = stackalloc int[4];
        for (var trial = 0; trial < 50; trial++)
        {
            var (a, refs) = MotionSadTestHelpers.RandomFour(rng, strideA, rowsA, strideB, rowsB);
            H264MotionSad.Sad16x16x4Scalar(a, strideA, refs[0], refs[1], refs[2], refs[3], strideB, sads);
            for (var c = 0; c < 4; c++)
            {
                sads[c].Should().Be(MotionSadTestHelpers.NaiveSad(a, strideA, refs[c], strideB, 16, 16));
            }

            if (AdvSimd.IsSupported)
            {
                H264MotionSad.Sad16x16x4AdvSimd(a, strideA, refs[0], refs[1], refs[2], refs[3], strideB, simd);
                for (var c = 0; c < 4; c++)
                {
                    simd[c].Should().Be(sads[c]);
                }
            }
        }
    }

    [Theory]
    [InlineData(13, 8, 11, 8)]
    [InlineData(8, 8, 8, 8)]
    public void Sad8x8x4_matches_four_scalar_calls(int strideA, int rowsA, int strideB, int rowsB)
    {
        var rng = new Random(0x4F02);
        Span<int> sads = stackalloc int[4];
        Span<int> simd = stackalloc int[4];
        for (var trial = 0; trial < 50; trial++)
        {
            var (a, refs) = MotionSadTestHelpers.RandomFour(rng, strideA, rowsA, strideB, rowsB);
            H264MotionSad.Sad8x8x4Scalar(a, strideA, refs[0], refs[1], refs[2], refs[3], strideB, sads);
            for (var c = 0; c < 4; c++)
            {
                sads[c].Should().Be(MotionSadTestHelpers.NaiveSad(a, strideA, refs[c], strideB, 8, 8));
            }

            if (AdvSimd.IsSupported)
            {
                H264MotionSad.Sad8x8x4AdvSimd(a, strideA, refs[0], refs[1], refs[2], refs[3], strideB, simd);
                for (var c = 0; c < 4; c++)
                {
                    simd[c].Should().Be(sads[c]);
                }
            }
        }
    }

    [Fact]
    public void Sad16x16x4_extreme_patterns()
    {
        if (!AdvSimd.IsSupported)
        {
            return;
        }

        const int stride = 24;
        var zeros = new byte[stride * 16];
        var ones = new byte[stride * 16];
        Array.Fill(ones, (byte)255);
        var mixed = new byte[stride * 16];
        for (var i = 0; i < mixed.Length; i++)
        {
            mixed[i] = (byte)(i * 37);
        }

        Span<int> sads = stackalloc int[4];

        H264MotionSad.Sad16x16x4AdvSimd(zeros, stride, zeros, zeros, zeros, zeros, stride, sads);
        sads.ToArray().Should().Equal(0, 0, 0, 0);

        H264MotionSad.Sad16x16x4AdvSimd(zeros, stride, ones, ones, ones, ones, stride, sads);
        sads.ToArray().Should().Equal(255 * 16 * 16, 255 * 16 * 16, 255 * 16 * 16, 255 * 16 * 16);

        H264MotionSad.Sad16x16x4AdvSimd(zeros, stride, ones, zeros, mixed, ones, stride, sads);
        sads[0].Should().Be(255 * 16 * 16);
        sads[1].Should().Be(0);
        sads[2].Should().Be(MotionSadTestHelpers.NaiveSad(zeros, stride, mixed, stride, 16, 16));
        sads[3].Should().Be(255 * 16 * 16);
    }

    [Fact]
    public void Sad8x8x4_extreme_patterns()
    {
        if (!AdvSimd.IsSupported)
        {
            return;
        }

        const int stride = 16;
        var zeros = new byte[stride * 8];
        var ones = new byte[stride * 8];
        Array.Fill(ones, (byte)255);
        var mixed = new byte[stride * 8];
        for (var i = 0; i < mixed.Length; i++)
        {
            mixed[i] = (byte)(i * 37);
        }

        Span<int> sads = stackalloc int[4];

        H264MotionSad.Sad8x8x4AdvSimd(zeros, stride, zeros, zeros, zeros, zeros, stride, sads);
        sads.ToArray().Should().Equal(0, 0, 0, 0);

        H264MotionSad.Sad8x8x4AdvSimd(zeros, stride, ones, ones, ones, ones, stride, sads);
        sads.ToArray().Should().Equal(255 * 8 * 8, 255 * 8 * 8, 255 * 8 * 8, 255 * 8 * 8);

        H264MotionSad.Sad8x8x4AdvSimd(ones, stride, mixed, zeros, ones, mixed, stride, sads);
        sads[0].Should().Be(MotionSadTestHelpers.NaiveSad(ones, stride, mixed, stride, 8, 8));
        sads[1].Should().Be(255 * 8 * 8);
        sads[2].Should().Be(0);
        sads[3].Should().Be(MotionSadTestHelpers.NaiveSad(ones, stride, mixed, stride, 8, 8));
    }

    [Fact]
    public void Resolved_kernel_set_x4_matches_scalar_set()
    {
        var best = H264KernelSet.CreateBest();
        var scalar = new ScalarKernelSet();
        var rng = new Random(0x4F03);

        Span<int> got = stackalloc int[4];
        Span<int> want = stackalloc int[4];
        for (var trial = 0; trial < 50; trial++)
        {
            var (a16, refs16) = MotionSadTestHelpers.RandomFour(rng, 24, 18, 22, 18);
            best.Sad16x16x4(a16, 24, refs16[0], refs16[1], refs16[2], refs16[3], 22, got);
            scalar.Sad16x16x4(a16, 24, refs16[0], refs16[1], refs16[2], refs16[3], 22, want);
            got.ToArray().Should().Equal(want.ToArray());

            var (a8, refs8) = MotionSadTestHelpers.RandomFour(rng, 16, 12, 14, 12);
            best.Sad8x8x4(a8, 16, refs8[0], refs8[1], refs8[2], refs8[3], 14, got);
            scalar.Sad8x8x4(a8, 16, refs8[0], refs8[1], refs8[2], refs8[3], 14, want);
            got.ToArray().Should().Equal(want.ToArray());
        }
    }

    [Theory]
    [InlineData(21, 18, 19, 18)]
    [InlineData(18, 17, 17, 17)]
    public void Resolved_kernel_set_x4_matches_scalar_set_for_unaligned_strides(
        int strideA, int rowsA, int strideB, int rowsB)
    {
        var best = H264KernelSet.CreateBest();
        var scalar = new ScalarKernelSet();
        var rng = new Random(0x4F04);

        Span<int> got = stackalloc int[4];
        Span<int> want = stackalloc int[4];
        for (var trial = 0; trial < 50; trial++)
        {
            var (a16, refs16) = MotionSadTestHelpers.RandomFour(rng, strideA, rowsA, strideB, rowsB);
            best.Sad16x16x4(a16, strideA, refs16[0], refs16[1], refs16[2], refs16[3], strideB, got);
            scalar.Sad16x16x4(a16, strideA, refs16[0], refs16[1], refs16[2], refs16[3], strideB, want);
            got.ToArray().Should().Equal(want.ToArray());

            var (a8, refs8) = MotionSadTestHelpers.RandomFour(rng, 13, 10, 11, 10);
            best.Sad8x8x4(a8, 13, refs8[0], refs8[1], refs8[2], refs8[3], 11, got);
            scalar.Sad8x8x4(a8, 13, refs8[0], refs8[1], refs8[2], refs8[3], 11, want);
            got.ToArray().Should().Equal(want.ToArray());
        }
    }

    // ── x64 (SSE2/SSSE3) four-candidate parity ──────────────────────────────────────────────────

    [Theory]
    [InlineData(24, 18, 22, 18)]
    [InlineData(21, 16, 19, 16)]
    [InlineData(33, 18, 24, 16)]
    public void Sad16x16x4_sse2_matches_scalar_when_available(int strideA, int rowsA, int strideB, int rowsB)
    {
        if (!Ssse3.IsSupported)
        {
            return;
        }

        EncoderSimdTestFacts.RequiresX64EncoderSimd();

        var rng = new Random(0x4E51);
        Span<int> want = stackalloc int[4];
        Span<int> got = stackalloc int[4];
        for (var trial = 0; trial < 50; trial++)
        {
            var (a, refs) = MotionSadTestHelpers.RandomFour(rng, strideA, rowsA, strideB, rowsB);
            H264MotionSad.Sad16x16x4Scalar(a, strideA, refs[0], refs[1], refs[2], refs[3], strideB, want);
            H264MotionSad.Sad16x16x4Sse2(a, strideA, refs[0], refs[1], refs[2], refs[3], strideB, got);
            got.ToArray().Should().Equal(want.ToArray());
            for (var c = 0; c < 4; c++)
            {
                got[c].Should().Be(MotionSadTestHelpers.NaiveSad(a, strideA, refs[c], strideB, 16, 16));
            }
        }
    }

    [Theory]
    [InlineData(16, 12, 14, 12)]
    [InlineData(13, 10, 11, 10)]
    [InlineData(9, 9, 11, 9)]
    public void Sad8x8x4_sse2_matches_scalar_when_available(int strideA, int rowsA, int strideB, int rowsB)
    {
        if (!Ssse3.IsSupported)
        {
            return;
        }

        EncoderSimdTestFacts.RequiresX64EncoderSimd();

        var rng = new Random(0x4E52);
        Span<int> want = stackalloc int[4];
        Span<int> got = stackalloc int[4];
        for (var trial = 0; trial < 50; trial++)
        {
            var (a, refs) = MotionSadTestHelpers.RandomFour(rng, strideA, rowsA, strideB, rowsB);
            H264MotionSad.Sad8x8x4Scalar(a, strideA, refs[0], refs[1], refs[2], refs[3], strideB, want);
            H264MotionSad.Sad8x8x4Sse2(a, strideA, refs[0], refs[1], refs[2], refs[3], strideB, got);
            got.ToArray().Should().Equal(want.ToArray());
            for (var c = 0; c < 4; c++)
            {
                got[c].Should().Be(MotionSadTestHelpers.NaiveSad(a, strideA, refs[c], strideB, 8, 8));
            }
        }
    }

    [Fact]
    public void Sad16x16x4_sse2_extreme_patterns()
    {
        if (!Ssse3.IsSupported)
        {
            return;
        }

        EncoderSimdTestFacts.RequiresX64EncoderSimd();

        const int stride = 24;
        var zeros = new byte[stride * 16];
        var ones = new byte[stride * 16];
        Array.Fill(ones, (byte)255);
        var mixed = new byte[stride * 16];
        for (var i = 0; i < mixed.Length; i++)
        {
            mixed[i] = (byte)(i * 37);
        }

        Span<int> sads = stackalloc int[4];

        H264MotionSad.Sad16x16x4Sse2(zeros, stride, zeros, zeros, zeros, zeros, stride, sads);
        sads.ToArray().Should().Equal(0, 0, 0, 0);

        // Max-difference pattern: every source sample is 255 from its candidate sample, the 16-bit
        // worst case for each accumulator lane.
        H264MotionSad.Sad16x16x4Sse2(zeros, stride, ones, ones, ones, ones, stride, sads);
        sads.ToArray().Should().Equal(255 * 16 * 16, 255 * 16 * 16, 255 * 16 * 16, 255 * 16 * 16);

        H264MotionSad.Sad16x16x4Sse2(zeros, stride, ones, zeros, mixed, ones, stride, sads);
        sads[0].Should().Be(255 * 16 * 16);
        sads[1].Should().Be(0);
        sads[2].Should().Be(MotionSadTestHelpers.NaiveSad(zeros, stride, mixed, stride, 16, 16));
        sads[3].Should().Be(255 * 16 * 16);
    }

    [Fact]
    public void Sad8x8x4_sse2_extreme_patterns()
    {
        if (!Ssse3.IsSupported)
        {
            return;
        }

        EncoderSimdTestFacts.RequiresX64EncoderSimd();

        const int stride = 16;
        var zeros = new byte[stride * 8];
        var ones = new byte[stride * 8];
        Array.Fill(ones, (byte)255);
        var mixed = new byte[stride * 8];
        for (var i = 0; i < mixed.Length; i++)
        {
            mixed[i] = (byte)(i * 37);
        }

        Span<int> sads = stackalloc int[4];

        H264MotionSad.Sad8x8x4Sse2(zeros, stride, zeros, zeros, zeros, zeros, stride, sads);
        sads.ToArray().Should().Equal(0, 0, 0, 0);

        H264MotionSad.Sad8x8x4Sse2(zeros, stride, ones, ones, ones, ones, stride, sads);
        sads.ToArray().Should().Equal(255 * 8 * 8, 255 * 8 * 8, 255 * 8 * 8, 255 * 8 * 8);

        H264MotionSad.Sad8x8x4Sse2(ones, stride, mixed, zeros, ones, mixed, stride, sads);
        sads[0].Should().Be(MotionSadTestHelpers.NaiveSad(ones, stride, mixed, stride, 8, 8));
        sads[1].Should().Be(255 * 8 * 8);
        sads[2].Should().Be(0);
        sads[3].Should().Be(MotionSadTestHelpers.NaiveSad(ones, stride, mixed, stride, 8, 8));
    }
}
