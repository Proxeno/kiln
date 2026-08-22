using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Ensures resolved <see cref="IH264KernelSet"/> tiers agree with <see cref="ScalarKernelSet"/> on
/// intra 4×4 prediction (modes 0..8). Isolates encode SIMD/scalar drift to the predict path.
/// </summary>
public sealed class H264KernelSetPredictParityTests
{
    private const int TopRowLength = 9;
    private const int LeftColLength = 4;

    public static IEnumerable<object[]> ModeAvailability()
    {
        bool[] flags = [true, false];
        for (var mode = 0; mode <= 8; mode++)
        {
            foreach (var topAvail in flags)
            {
                foreach (var leftAvail in flags)
                {
                    if (mode == 1 && !leftAvail) continue;
                    if (mode == 0 && !topAvail) continue;
                    if (mode is 3 or 4 or 5 or 6 or 7 or 8 && (!topAvail || !leftAvail)) continue;
                    yield return [mode, topAvail, leftAvail];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(ModeAvailability))]
    public void Predict4x4_scalar_kernel_set_matches_create_best(int mode, bool topAvail, bool leftAvail)
    {
        var best = H264KernelSet.CreateBest();
        if (best is ScalarKernelSet)
        {
            return;
        }

        var rng = new Random(mode * 17 + (topAvail ? 1 : 0) + (leftAvail ? 2 : 0));
        var topRow = new byte[TopRowLength];
        var leftCol = new byte[LeftColLength];
        rng.NextBytes(topRow);
        rng.NextBytes(leftCol);

        Span<byte> scalar = stackalloc byte[16];
        Span<byte> simd = stackalloc byte[16];
        new ScalarKernelSet().Predict4x4(mode, topRow, leftCol, topAvail, leftAvail, scalar);
        best.Predict4x4(mode, topRow, leftCol, topAvail, leftAvail, simd);

        for (var i = 0; i < 16; i++)
        {
            simd[i].Should().Be(scalar[i],
                $"mode={mode} i={i} topAvail={topAvail} leftAvail={leftAvail}");
        }
    }
}
