using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Phase 3a parity gate: every SIMD Intra_16×16 prediction kernel must produce byte-identical
/// output to the scalar reference for all four modes across the cartesian product of
/// (neighbor-availability, RNG seed, hand-crafted fixture). Skips on non-SIMD hosts.
/// </summary>
public sealed class H264Intra16x16PredictSimdParityTests
{
    public static IEnumerable<object[]> AllModeSeedAvailability()
    {
        int[] seeds = [1, 2, 3, 5, 7, 11, 13, 17, 19, 23];
        bool[] flags = [true, false];
        for (var mode = 0; mode <= 3; mode++)
        {
            foreach (var seed in seeds)
            {
                foreach (var topAvail in flags)
                {
                    foreach (var leftAvail in flags)
                    {
                        foreach (var topLeftAvail in flags)
                        {
                            // Skip combinations that are invalid per mode requirements:
                            // mode 0 needs top, mode 1 needs left, mode 3 needs all three.
                            if (mode == 0 && !topAvail) continue;
                            if (mode == 1 && !leftAvail) continue;
                            if (mode == 3 && (!topAvail || !leftAvail || !topLeftAvail)) continue;
                            yield return [mode, seed, topAvail, leftAvail, topLeftAvail];
                        }
                    }
                }
            }
        }
    }

    private static (byte[] topRow, byte[] leftCol, byte topLeft) RngFixture(int seed)
    {
        var rng = new Random(seed);
        var topRow = new byte[16];
        var leftCol = new byte[16];
        rng.NextBytes(topRow);
        rng.NextBytes(leftCol);
        var topLeft = (byte)rng.Next(256);
        return (topRow, leftCol, topLeft);
    }

    [Theory]
    [MemberData(nameof(AllModeSeedAvailability))]
    public void Simd_predict_matches_scalar_rng(
        int mode, int seed, bool topAvail, bool leftAvail, bool topLeftAvail)
    {
        if (!H264Intra16x16PredictionSimd.IsSupported)
        {
            return;
        }

        var (topRow, leftCol, topLeft) = RngFixture(seed);

        Span<byte> scalar = stackalloc byte[256];
        Span<byte> simd = stackalloc byte[256];

        using (new H264IntrinsicsPreference.Scope(false))
        {
            H264Intra16x16Prediction.Predict(
                mode, topRow, topAvail, leftCol, leftAvail, topLeft, topLeftAvail, scalar);
        }

        using (new H264IntrinsicsPreference.Scope(true))
        {
            H264Intra16x16Prediction.Predict(
                mode, topRow, topAvail, leftCol, leftAvail, topLeft, topLeftAvail, simd);
        }

        for (var i = 0; i < 256; i++)
        {
            simd[i].Should().Be(scalar[i],
                $"mode={mode} seed={seed} topAvail={topAvail} leftAvail={leftAvail} " +
                $"topLeftAvail={topLeftAvail} pos=({i % 16},{i / 16})");
        }
    }

    public static IEnumerable<object[]> HandCraftedFixtures()
    {
        foreach (var mode in new[] { 0, 1, 2, 3 })
        {
            yield return [mode, "uniform_128"];
            yield return [mode, "gradient"];
            yield return [mode, "high_contrast"];
        }
    }

    [Theory]
    [MemberData(nameof(HandCraftedFixtures))]
    public void Simd_predict_matches_scalar_hand_crafted(int mode, string fixtureName)
    {
        if (!H264Intra16x16PredictionSimd.IsSupported)
        {
            return;
        }

        var (topRow, leftCol, topLeft) = fixtureName switch
        {
            "uniform_128" => (Fill(128), Fill(128), (byte)128),
            "gradient"    => (Range(16, 16, x => (byte)(x * 16)), Range(0, 16, y => (byte)(y * 16)), (byte)0),
            "high_contrast" => (
                new byte[] { 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0 },
                new byte[] { 0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255 },
                (byte)128),
            _ => throw new ArgumentOutOfRangeException(nameof(fixtureName)),
        };

        foreach (var topAvail in new[] { true, false })
        {
            foreach (var leftAvail in new[] { true, false })
            {
                if (mode == 0 && !topAvail) continue;
                if (mode == 1 && !leftAvail) continue;
                if (mode == 3 && (!topAvail || !leftAvail)) continue;

                var scalar = new byte[256];
                var simd = new byte[256];

                using (new H264IntrinsicsPreference.Scope(false))
                {
                    H264Intra16x16Prediction.Predict(
                        mode, topRow, topAvail, leftCol, leftAvail, topLeft, topLeftAvail: true, scalar);
                }

                using (new H264IntrinsicsPreference.Scope(true))
                {
                    H264Intra16x16Prediction.Predict(
                        mode, topRow, topAvail, leftCol, leftAvail, topLeft, topLeftAvail: true, simd);
                }

                for (var i = 0; i < 256; i++)
                {
                    simd[i].Should().Be(scalar[i],
                        $"fixture={fixtureName} mode={mode} topAvail={topAvail} leftAvail={leftAvail} " +
                        $"pos=({i % 16},{i / 16})");
                }
            }
        }
    }

    [Fact]
    public void Sad16x16_simd_matches_scalar()
    {
        if (!H264Intra16x16PredictionSimd.IsSupported)
        {
            return;
        }

        var rng = new Random(42);
        var src = new byte[256];
        var pred = new byte[256];
        rng.NextBytes(src);
        rng.NextBytes(pred);

        var scalarSad = 0;
        for (var i = 0; i < 256; i++) scalarSad += Math.Abs(src[i] - pred[i]);

        var simdSad = H264Intra16x16PredictionSimd.Sad16x16(src, pred, srcStride: 16);
        simdSad.Should().Be(scalarSad);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(50)]
    public void Sad16x16_simd_matches_scalar_rng_seeds(int seed)
    {
        if (!H264Intra16x16PredictionSimd.IsSupported)
        {
            return;
        }

        var rng = new Random(seed);
        var src = new byte[256];
        var pred = new byte[256];
        rng.NextBytes(src);
        rng.NextBytes(pred);

        var scalarSad = 0;
        for (var i = 0; i < 256; i++) scalarSad += Math.Abs(src[i] - pred[i]);

        var simdSad = H264Intra16x16PredictionSimd.Sad16x16(src, pred, srcStride: 16);
        simdSad.Should().Be(scalarSad, $"seed={seed}");
    }

    private static byte[] Fill(int v)
    {
        var a = new byte[16];
        Array.Fill(a, (byte)v);
        return a;
    }

    private static byte[] Range(int start, int count, Func<int, byte> f)
    {
        var a = new byte[count];
        for (var i = 0; i < count; i++) a[i] = f(start + i);
        return a;
    }
}
