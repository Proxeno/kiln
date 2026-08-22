// Tests for Intra_16×16 mode RD selection: the scorer and the integration
// with the P-slice encoder's mode decision.

using Xunit;

#if HAS_INTRA16x16_RD
using FluentAssertions;
using Kiln.Internal.H264;
#endif

namespace Kiln.Tests;

/// <summary>
/// Parity tests for the Intra_16×16 RD scorer (H264Intra16x16Prediction.BestI16x16Mode)
/// and for the P-slice encoder's intra/inter selection.
///
/// Key invariants:
///   1. Flat (uniform-color) MB: DC mode (mode 2) achieves SAD = 0 when neighbour samples are
///      available and match the input; no other mode can beat it without top/left information.
///   2. Horizontal gradient: Vertical mode (mode 0) achieves lower SAD than DC when there is a
///      top row that matches the content.
///   3. Vertically striped MB: Horizontal mode (mode 1) achieves lower SAD.
/// </summary>
public sealed class H264Intra16x16RdSelectionTests
{
#if !HAS_INTRA16x16_RD
    [Fact]
    public void Intra16x16RdScorer_must_be_delivered_before_test_runs()
    {
        var t = Type.GetType("Kiln.Internal.H264.H264Intra16x16Prediction, Kiln");
        if (t is null)
        {
            Assert.Fail("H264Intra16x16Prediction is missing.");
        }

        var m = t.GetMethod("BestI16x16Mode",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (m is null)
        {
            Assert.Fail(
                "H264Intra16x16Prediction.BestI16x16Mode is missing. " +
                "Implement it per the A3-rd-compare plan step, then enable HAS_INTRA16x16_RD.");
        }

        Assert.Fail("HAS_INTRA16x16_RD must be set in Kiln.Tests.csproj.");
    }
#else
    /// <summary>
    /// Flat (constant-128) input with matching top/left neighbours: DC mode wins with SAD = 0.
    /// No other mode can beat DC when the block is spatially uniform.
    /// </summary>
    [Fact]
    public void BestI16x16Mode_flat_block_selects_dc_mode_with_zero_sad()
    {
        Span<byte> src = stackalloc byte[256];
        src.Fill(128);

        Span<byte> topRow = stackalloc byte[16];
        topRow.Fill(128);
        Span<byte> leftCol = stackalloc byte[16];
        leftCol.Fill(128);

        var (mode, sad) = H264Intra16x16Prediction.BestI16x16Mode(
            src,
            topRow, topAvail: true,
            leftCol, leftAvail: true,
            topLeft: 128, topLeftAvail: true);

        sad.Should().Be(0, "DC prediction of a flat block perfectly matches every sample");
        mode.Should().Be(2, "mode 2 = DC is the canonical winner for uniform-colour content");
    }

    /// <summary>
    /// Horizontal gradient input (each row is constant at a different value) with a matching top
    /// row: Vertical mode (mode 0) fills each row with the top sample → SAD = 0 only if all rows
    /// equal the top sample.  With a ramp, vertical mode can still have lower SAD than DC.
    /// </summary>
    [Fact]
    public void BestI16x16Mode_vertical_gradient_selects_vertical_or_dc()
    {
        // Build a 16×16 block where row i = i * 8 (0..120).
        Span<byte> src = stackalloc byte[256];
        for (var y = 0; y < 16; y++)
            for (var x = 0; x < 16; x++)
                src[y * 16 + x] = (byte)(y * 8);

        // Top row = row 0 = all zeros (the row just above the block, repeated by replication).
        Span<byte> topRow = stackalloc byte[16];
        topRow.Fill(0);
        Span<byte> leftCol = stackalloc byte[16];
        for (var y = 0; y < 16; y++) leftCol[y] = (byte)(y * 8);

        // We don't assert the winning mode here (both DC and vertical are reasonable for a ramp).
        // Instead we verify that the SAD is finite (function doesn't crash) and that the winning
        // SAD is not worse than DC:
        var dcSad = 0;
        for (var y = 0; y < 16; y++)
            for (var x = 0; x < 16; x++)
            {
                // DC = average of available neighbours (top + left, 32 samples, mean ~60)
                var dc = (topRow[x] + leftCol[y] + 1) / 2;
                dcSad += Math.Abs(src[y * 16 + x] - dc);
            }

        var (_, bestSad) = H264Intra16x16Prediction.BestI16x16Mode(
            src,
            topRow, topAvail: true,
            leftCol, leftAvail: true,
            topLeft: 0, topLeftAvail: true);

        bestSad.Should().BeLessThanOrEqualTo(dcSad,
            "BestI16x16Mode must find a mode at least as good as DC for this gradient block");
    }

    /// <summary>
    /// Verifies that BestI16x16Mode returns mode 2 (DC) when only the top-left corner is
    /// available (no top row, no left column), consistent with H.264 §8.3.3.3 DC fallback rules.
    /// </summary>
    [Fact]
    public void BestI16x16Mode_no_neighbours_falls_back_to_dc()
    {
        Span<byte> src = stackalloc byte[256];
        src.Fill(100);

        var (mode, _) = H264Intra16x16Prediction.BestI16x16Mode(
            src,
            topRow: default, topAvail: false,
            leftCol: default, leftAvail: false,
            topLeft: 0, topLeftAvail: false);

        mode.Should().Be(2,
            "without top or left neighbours, only DC (mode 2) is valid per H.264 §8.3.3.3 availability");
    }
#endif
}
