using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Phase 1 senior parity test gating Junior J5 (fill half). Asserts that the (yet-to-land) SIMD
/// vertical/horizontal/DC fill helper <see cref="H264Intra4X4FillSimd.Predict"/> produces
/// byte-identical output to the scalar oracle <see cref="H264Intra4X4Prediction.Predict"/> for
/// modes 0..2 across the cartesian product of (mode, RNG seed, neighbour-availability), plus DC's
/// special "neither neighbour available ⇒ broadcast 128" fallback. This file is intentionally
/// compile-failing on <c>main</c>; J5's mandatory pre-check (rule 11) requires the test class to
/// exist before they implement <c>H264Intra4X4FillSimd</c>. Skips on non-SIMD hosts.
/// J5's quant-teardown half is covered by
/// <c>H264SimdParityTests.Quant4X4_simd_matches_scalar_when_intrinsics_available</c>.
/// </summary>
public sealed class H264Intra4x4FillSimdTests
{
    private const int TopRowLength = 9;
    private const int LeftColLength = 4;

    public static IEnumerable<object[]> ModeSeedAvailability()
    {
        int[] seeds = [1, 2, 3, 5, 7, 11, 13, 17, 19, 23];
        bool[] flags = [true, false];
        for (var mode = 0; mode <= 2; mode++)
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
        H264Intra4X4FillSimd.Predict(mode, topRow, leftCol, topAvail, leftAvail, actual);
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
        if (!H264Intra4X4FillSimd.IsSupported)
        {
            return;
        }

        var (topRow, leftCol) = RngFixture(seed);
        AssertModeParity(mode, topRow, leftCol, topAvail, leftAvail, $"rng_seed={seed}");
    }

    public static IEnumerable<object[]> DcSpecialCases()
    {
        var rngTop = new byte[TopRowLength];
        var rngLeft = new byte[LeftColLength];
        new Random(42).NextBytes(rngTop);
        new Random(43).NextBytes(rngLeft);
        yield return ["dc_top_only", rngTop, rngLeft, true, false];
        yield return ["dc_left_only", rngTop, rngLeft, false, true];
        yield return ["dc_neither", rngTop, rngLeft, false, false];
    }

    /// <summary>DC mode 2 with a single neighbour and with neither neighbour (must broadcast 128).</summary>
    [Theory]
    [MemberData(nameof(DcSpecialCases))]
    public void Predict_dc_simd_matches_scalar_for_availability_edge_cases(
        string label, byte[] topRow, byte[] leftCol, bool topAvail, bool leftAvail)
    {
        if (!H264Intra4X4FillSimd.IsSupported)
        {
            return;
        }

        AssertModeParity(2, topRow, leftCol, topAvail, leftAvail, label);
    }

    /// <summary>Vertical (0) and Horizontal (1) modes with their respective neighbour absent
    /// (must fall through to DC like the scalar oracle does).</summary>
    [Theory]
    [InlineData(0, false, true)]
    [InlineData(1, true, false)]
    public void Predict_vh_simd_matches_scalar_when_required_neighbour_absent(
        int mode, bool topAvail, bool leftAvail)
    {
        if (!H264Intra4X4FillSimd.IsSupported)
        {
            return;
        }

        var rng = new Random(31);
        var topRow = new byte[TopRowLength];
        var leftCol = new byte[LeftColLength];
        rng.NextBytes(topRow);
        rng.NextBytes(leftCol);
        AssertModeParity(mode, topRow, leftCol, topAvail, leftAvail, $"mode{mode}_neighbour_absent");
    }
}
