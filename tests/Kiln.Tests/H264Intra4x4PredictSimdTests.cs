using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Phase 1 senior parity test gating Junior J4. Asserts that the (yet-to-land) directional SIMD
/// predictor <see cref="H264Intra4X4DirectionalSimd.Predict"/> produces byte-identical output to the
/// scalar oracle <see cref="H264Intra4X4Prediction.Predict"/> for modes 3..8 across the cartesian
/// product of (mode, RNG seed, neighbour-availability). Uses per-position assertion messages so a
/// single sample bug pinpoints which spec branch diverged. This file is intentionally compile-failing
/// on <c>main</c>; J4's mandatory pre-check (rule 11) requires the test class to exist before they
/// implement <c>H264Intra4X4DirectionalSimd</c>. Skips on non-SIMD hosts.
/// </summary>
public sealed class H264Intra4x4PredictSimdTests
{
    private const int TopRowLength = 9;
    private const int LeftColLength = 4;

    public static IEnumerable<object[]> ModeSeedAvailability()
    {
        int[] seeds = [1, 2, 3, 5, 7, 11, 13, 17, 19, 23];
        bool[] flags = [true, false];
        for (var mode = 3; mode <= 8; mode++)
        {
            foreach (var seed in seeds)
            {
                foreach (var topAvail in flags)
                {
                    foreach (var leftAvail in flags)
                    {
                        yield return [mode, seed, topAvail, leftAvail];
                    }
                }
            }
        }
    }

    private static (byte[] topRow, byte[] leftCol) RngFixture(int seed)
    {
        var rng = new Random(seed);
        var topRow = new byte[TopRowLength];
        var leftCol = new byte[LeftColLength];
        rng.NextBytes(topRow);
        rng.NextBytes(leftCol);
        return (topRow, leftCol);
    }

    private static void AssertModeParity(
        int mode,
        ReadOnlySpan<byte> topRow,
        ReadOnlySpan<byte> leftCol,
        bool topAvail,
        bool leftAvail,
        string fixtureLabel)
    {
        Span<byte> expected = stackalloc byte[16];
        Span<byte> actual = stackalloc byte[16];
        H264Intra4X4Prediction.Predict(mode, topRow, leftCol, topAvail, leftAvail, expected);
        H264Intra4X4DirectionalSimd.Predict(mode, topRow, leftCol, topAvail, leftAvail, actual);
        for (var i = 0; i < 16; i++)
        {
            actual[i].Should().Be(expected[i],
                $"fixture={fixtureLabel} mode={mode} pos={i} (x={i % 4}, y={i / 4}) " +
                $"topAvail={topAvail} leftAvail={leftAvail}");
        }
    }

    [Theory]
    [MemberData(nameof(ModeSeedAvailability))]
    public void Predict_simd_matches_scalar_for_rng_seeded_neighbours(
        int mode, int seed, bool topAvail, bool leftAvail)
    {
        if (!H264Intra4X4DirectionalSimd.IsSupported)
        {
            return;
        }

        var (topRow, leftCol) = RngFixture(seed);
        AssertModeParity(mode, topRow, leftCol, topAvail, leftAvail, $"rng_seed={seed}");
    }

    public static IEnumerable<object[]> HandCraftedFixtures()
    {
        for (var mode = 3; mode <= 8; mode++)
        {
            yield return [mode, "uniform_128"];
            yield return [mode, "gradient"];
            yield return [mode, "sharp_edge"];
        }
    }

    [Theory]
    [MemberData(nameof(HandCraftedFixtures))]
    public void Predict_simd_matches_scalar_for_hand_crafted_fixtures(int mode, string fixtureName)
    {
        if (!H264Intra4X4DirectionalSimd.IsSupported)
        {
            return;
        }

        var (topRow, leftCol) = fixtureName switch
        {
            "uniform_128" => (Repeat(TopRowLength, (byte)128), Repeat(LeftColLength, (byte)128)),
            "gradient" => (
                new byte[] { 16, 32, 48, 64, 80, 96, 112, 128, 144 },
                new byte[] { 32, 48, 64, 80 }),
            "sharp_edge" => (
                new byte[] { 0, 255, 255, 255, 255, 255, 255, 255, 255 },
                new byte[] { 255, 0, 0, 0 }),
            _ => throw new ArgumentOutOfRangeException(nameof(fixtureName)),
        };

        foreach (var topAvail in new[] { true, false })
        {
            foreach (var leftAvail in new[] { true, false })
            {
                AssertModeParity(mode, topRow, leftCol, topAvail, leftAvail, fixtureName);
            }
        }
    }

    private static byte[] Repeat(int len, byte value)
    {
        var a = new byte[len];
        Array.Fill(a, value);
        return a;
    }
}
