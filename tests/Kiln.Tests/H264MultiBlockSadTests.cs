using FluentAssertions;
using Kiln.Internal.H264;
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
}
