using FluentAssertions;
using Kiln.Internal.H264;
using System.Runtime.Intrinsics.X86;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// AVX2 motion SAD (<see cref="H264MotionSad"/> 16-wide rows) must match scalar bit-exact.
/// </summary>
public sealed class H264MotionSadAvx2ParityTests
{
    [Fact]
    public void Sad16x16_avx2_matches_scalar_when_available()
    {
        EncoderSimdTestFacts.RequiresX64Avx2MotionSad();
        if (!Avx2.IsSupported)
        {
            return;
        }

        var rng = new Random(0xA7A2);
        for (var trial = 0; trial < 50; trial++)
        {
            var (a, b) = MotionSadTestHelpers.RandomPair(rng, 24, 18, 22, 20);
            var avx2 = H264MotionSad.Sad16x16Avx2(a, 24, b, 22);
            var scalar = H264MotionSad.Sad16x16Scalar(a, 24, b, 22);
            avx2.Should().Be(scalar);
            avx2.Should().Be(MotionSadTestHelpers.NaiveSad(a, 24, b, 22, 16, 16));
        }
    }

    [Fact]
    public void Sad16x8_avx2_matches_scalar_when_available()
    {
        EncoderSimdTestFacts.RequiresX64Avx2MotionSad();
        if (!Avx2.IsSupported)
        {
            return;
        }

        var rng = new Random(0xA7A3);
        for (var trial = 0; trial < 50; trial++)
        {
            var (a, b) = MotionSadTestHelpers.RandomPair(rng, 24, 12, 22, 14);
            H264MotionSad.Sad16x8Avx2(a, 24, b, 22).Should().Be(H264MotionSad.Sad16x8Scalar(a, 24, b, 22));
        }
    }

    [Fact]
    public void Sad8x16_avx2_matches_scalar_when_available()
    {
        EncoderSimdTestFacts.RequiresX64Avx2MotionSad();
        if (!Avx2.IsSupported)
        {
            return;
        }

        var rng = new Random(0xA7A4);
        for (var trial = 0; trial < 50; trial++)
        {
            var (a, b) = MotionSadTestHelpers.RandomPair(rng, 18, 20, 19, 18);
            H264MotionSad.Sad8x16Avx2(a, 18, b, 19).Should().Be(H264MotionSad.Sad8x16Scalar(a, 18, b, 19));
        }
    }

    [Fact]
    public void Sad8x8_avx2_matches_scalar_when_available()
    {
        EncoderSimdTestFacts.RequiresX64Avx2MotionSad();
        if (!Avx2.IsSupported)
        {
            return;
        }

        var rng = new Random(0xA7A5);
        for (var trial = 0; trial < 50; trial++)
        {
            var (a, b) = MotionSadTestHelpers.RandomPair(rng, 18, 10, 19, 11);
            H264MotionSad.Sad8x8Avx2(a, 18, b, 19).Should().Be(H264MotionSad.Sad8x8Scalar(a, 18, b, 19));
        }
    }

    [Fact]
    public void Sad16x16_dispatch_produces_correct_result_with_intrinsics_on()
    {
        if (!Avx2.IsSupported || !H264MotionSad.IsSupported)
        {
            return;
        }

        using (new H264IntrinsicsPreference.Scope(preferIntrinsics: true))
        {
            H264IntrinsicsPreference.UseMotionSadSimd.Should().BeTrue();
            H264MotionSad.IsAvx2MotionSadSupported.Should().BeTrue();

            var a = new byte[32 * 32];
            var b = new byte[32 * 32];
            Array.Fill(b, (byte)255);
            H264MotionSad.Sad16x16(a, 32, b, 32).Should().Be(255 * 16 * 16);
        }
    }

    [Fact]
    public void Sad16x16_prefers_scalar_when_intrinsics_disabled()
    {
        if (!H264MotionSad.IsSupported)
        {
            return;
        }

        var rng = new Random(0xB0D0);
        var (a, b) = MotionSadTestHelpers.RandomPair(rng, 20, 16, 18, 16);
        using (new H264IntrinsicsPreference.Scope(preferIntrinsics: false))
        {
            H264MotionSad.Sad16x16(a, 20, b, 18)
                .Should()
                .Be(H264MotionSad.Sad16x16Scalar(a, 20, b, 18));
        }
    }
}
