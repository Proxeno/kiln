// Parity tests for the SIMD qpel luma interpolation path (B2).
//
// Guard: tests compile and run only when HAS_QPEL_SIMD is defined, which signals that
// H264QpelLumaInterp.InterpolateSimd has been implemented. Until then the placeholder
// test fires, indicating the feature is not yet delivered.

using Xunit;

#if HAS_QPEL_SIMD
using FluentAssertions;
using Kiln.Internal.H264;
#endif

namespace Kiln.Tests;

/// <summary>
/// Parity tests verifying that the SIMD 6-tap qpel luma interpolation path
/// produces byte-exact output compared with the scalar
/// <see cref="H264QpelLumaInterp.Interpolate"/> reference for all 15 non-trivial
/// fractional positions and a suite of synthetic 16×16 source patches.
/// </summary>
public sealed class H264QpelLumaInterpSimdParityTests
{
#if !HAS_QPEL_SIMD
    [Fact]
    public void QpelLumaSimd_must_be_delivered_before_tests_run()
    {
        var t = Type.GetType("Kiln.Internal.H264.H264QpelLumaInterp, Kiln");
        if (t is null) { Assert.Fail("H264QpelLumaInterp not found in Kiln assembly."); return; }
        var m = t.GetMethod("InterpolateSimd",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (m is null)
        {
            Assert.Fail(
                "H264QpelLumaInterp.InterpolateSimd is missing. " +
                "Implement the SIMD 6-tap horizontal+vertical filter per the B2 plan step, " +
                "then enable HAS_QPEL_SIMD in Kiln.Tests.csproj.");
        }

        Assert.Fail("HAS_QPEL_SIMD must be set in Kiln.Tests.csproj.");
    }
#else
    /// <summary>
    /// For every non-trivial fractional position (xFrac, yFrac) from (0,0) to (3,3)
    /// (excluding (0,0) which is an integer copy) and 50 random source patches,
    /// the SIMD output is byte-exact with the scalar reference.
    /// </summary>
    [Fact]
    public void InterpolateSimd_matches_scalar_for_all_fractional_positions()
    {
        if (!H264QpelLumaInterp.IsSimdSupported) return;

        const int BlockW = 16, BlockH = 16;
        // 6-tap filter halo: 2 samples before origin, 3 samples after → 5 extra per axis.
        const int PatchW = BlockW + 5, PatchH = BlockH + 5;
        const int SrcOriginX = 2, SrcOriginY = 2;

        var rng = new Random(0xB2_0001);
        Span<byte> src = new byte[PatchH * PatchW];
        Span<byte> dstScalar = new byte[BlockH * BlockW];
        Span<byte> dstSimd   = new byte[BlockH * BlockW];

        for (var xFrac = 0; xFrac <= 3; xFrac++)
        {
            for (var yFrac = 0; yFrac <= 3; yFrac++)
            {
                if (xFrac == 0 && yFrac == 0) continue; // integer copy, trivially equal

                for (var trial = 0; trial < 50; trial++)
                {
                    rng.NextBytes(src);

                    H264QpelLumaInterp.Interpolate(
                        src, PatchW, SrcOriginX, SrcOriginY,
                        xFrac, yFrac, BlockW, BlockH, dstScalar, BlockW);

                    H264QpelLumaInterp.InterpolateSimd(
                        src, PatchW, SrcOriginX, SrcOriginY,
                        xFrac, yFrac, BlockW, BlockH, dstSimd, BlockW);

                    for (var i = 0; i < BlockH * BlockW; i++)
                        dstSimd[i].Should().Be(dstScalar[i],
                            $"xFrac={xFrac} yFrac={yFrac} trial={trial} pos={i}");
                }
            }
        }
    }

    /// <summary>
    /// Flat uniform source (all same value): every interpolated output must equal that value
    /// regardless of fractional position.
    /// </summary>
    [Theory]
    [InlineData(0, 1)] [InlineData(1, 0)] [InlineData(2, 2)] [InlineData(3, 3)]
    public void InterpolateSimd_uniform_source_yields_uniform_output(int xFrac, int yFrac)
    {
        if (!H264QpelLumaInterp.IsSimdSupported) return;

        const int PatchW = 21, PatchH = 21, BlockW = 16, BlockH = 16;
        Span<byte> src = new byte[PatchH * PatchW];
        src.Fill(128);
        Span<byte> dstSimd = new byte[BlockH * BlockW];

        H264QpelLumaInterp.InterpolateSimd(src, PatchW, 2, 2, xFrac, yFrac, BlockW, BlockH, dstSimd, BlockW);

        for (var i = 0; i < BlockH * BlockW; i++)
            dstSimd[i].Should().Be(128, $"xFrac={xFrac} yFrac={yFrac} pos={i}");
    }
#endif
}
