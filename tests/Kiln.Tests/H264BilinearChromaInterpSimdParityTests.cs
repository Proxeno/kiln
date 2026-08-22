// Parity tests for the SIMD bilinear chroma interpolation path (B3).
//
// Guard: tests compile and run only when HAS_BILINEAR_SIMD is defined, which signals that
// H264BilinearChromaInterp.InterpolateSimd has been implemented. Until then the placeholder
// test fires, indicating the feature is not yet delivered.

using Xunit;

#if HAS_BILINEAR_SIMD
using FluentAssertions;
using Kiln.Internal.H264;
#endif

namespace Kiln.Tests;

/// <summary>
/// Parity tests verifying that the SIMD bilinear chroma interpolation path produces
/// byte-exact output compared with the scalar <see cref="H264BilinearChromaInterp.Interpolate"/>
/// reference for all (dx,dy) fractional offsets (1/8-pel units, 0..7 each) and a suite of
/// random 8×8 chroma source blocks.
/// </summary>
public sealed class H264BilinearChromaInterpSimdParityTests
{
#if !HAS_BILINEAR_SIMD
    [Fact]
    public void BilinearChromaSimd_must_be_delivered_before_tests_run()
    {
        var t = Type.GetType("Kiln.Internal.H264.H264BilinearChromaInterp, Kiln");
        if (t is null) { Assert.Fail("H264BilinearChromaInterp not found in Kiln assembly."); return; }
        var m = t.GetMethod("InterpolateSimd",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (m is null)
        {
            Assert.Fail(
                "H264BilinearChromaInterp.InterpolateSimd is missing. " +
                "Implement the SIMD bilinear chroma filter per the B3 plan step, " +
                "then enable HAS_BILINEAR_SIMD in Kiln.Tests.csproj.");
        }

        Assert.Fail("HAS_BILINEAR_SIMD must be set in Kiln.Tests.csproj.");
    }
#else
    /// <summary>
    /// For every (xFrac, yFrac) in [0..7]×[0..7] and 50 random 8×8 source patches,
    /// the SIMD output is byte-exact with the scalar reference.
    /// </summary>
    [Fact]
    public void InterpolateSimd_matches_scalar_for_all_fractional_positions()
    {
        if (!H264BilinearChromaInterp.IsSimdSupported) return;

        const int BlockW = 8, BlockH = 8;
        // Bilinear needs 1-pel halo on right and bottom: patch is blockW+1 × blockH+1.
        const int PatchW = BlockW + 1, PatchH = BlockH + 1;
        const int SrcOriginX = 0, SrcOriginY = 0;

        var rng = new Random(0xB3_0001);
        Span<byte> src      = new byte[PatchH * PatchW];
        Span<byte> dstScalar = new byte[BlockH * BlockW];
        Span<byte> dstSimd   = new byte[BlockH * BlockW];

        for (var xFrac = 0; xFrac <= 7; xFrac++)
        {
            for (var yFrac = 0; yFrac <= 7; yFrac++)
            {
                for (var trial = 0; trial < 50; trial++)
                {
                    rng.NextBytes(src);

                    H264BilinearChromaInterp.Interpolate(
                        src, PatchW, SrcOriginX, SrcOriginY,
                        xFrac, yFrac, BlockW, BlockH, dstScalar, BlockW);

                    H264BilinearChromaInterp.InterpolateSimd(
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
    /// Flat uniform source (all same value): every bilinearly-interpolated output must equal that value
    /// for all fractional positions.
    /// </summary>
    [Theory]
    [InlineData(0, 0)] [InlineData(1, 0)] [InlineData(0, 1)] [InlineData(7, 7)]
    [InlineData(3, 5)] [InlineData(4, 4)]
    public void InterpolateSimd_uniform_source_yields_uniform_output(int xFrac, int yFrac)
    {
        if (!H264BilinearChromaInterp.IsSimdSupported) return;

        const int PatchW = 9, PatchH = 9, BlockW = 8, BlockH = 8;
        Span<byte> src = new byte[PatchH * PatchW];
        src.Fill(128);
        Span<byte> dstSimd = new byte[BlockH * BlockW];

        H264BilinearChromaInterp.InterpolateSimd(src, PatchW, 0, 0, xFrac, yFrac, BlockW, BlockH, dstSimd, BlockW);

        for (var i = 0; i < BlockH * BlockW; i++)
            dstSimd[i].Should().Be(128, $"xFrac={xFrac} yFrac={yFrac} pos={i}");
    }

    /// <summary>
    /// Also test 4×4 block dimensions (used for 4:2:0 8×8 chroma split into 4×4 partitions).
    /// </summary>
    [Fact]
    public void InterpolateSimd_matches_scalar_for_4x4_blocks()
    {
        if (!H264BilinearChromaInterp.IsSimdSupported) return;

        const int BlockW = 4, BlockH = 4;
        const int PatchW = BlockW + 1, PatchH = BlockH + 1;
        var rng = new Random(0xB3_0002);
        Span<byte> src       = new byte[PatchH * PatchW];
        Span<byte> dstScalar = new byte[BlockH * BlockW];
        Span<byte> dstSimd   = new byte[BlockH * BlockW];

        for (var xFrac = 0; xFrac <= 7; xFrac++)
        {
            for (var yFrac = 0; yFrac <= 7; yFrac++)
            {
                for (var trial = 0; trial < 20; trial++)
                {
                    rng.NextBytes(src);
                    H264BilinearChromaInterp.Interpolate(src, PatchW, 0, 0, xFrac, yFrac, BlockW, BlockH, dstScalar, BlockW);
                    H264BilinearChromaInterp.InterpolateSimd(src, PatchW, 0, 0, xFrac, yFrac, BlockW, BlockH, dstSimd, BlockW);
                    for (var i = 0; i < BlockH * BlockW; i++)
                        dstSimd[i].Should().Be(dstScalar[i], $"xFrac={xFrac} yFrac={yFrac} trial={trial} pos={i}");
                }
            }
        }
    }
#endif
}
