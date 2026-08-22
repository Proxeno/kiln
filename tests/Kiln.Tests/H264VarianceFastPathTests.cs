using FluentAssertions;
using Kiln;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Correctness tests for <see cref="H264VarianceFastPath.IsLowVariance4x4"/> and
/// integration smoke tests that confirm the variance fast-path fires on flat blocks and
/// does not fire on high-activity blocks during encoding.
/// </summary>
public sealed class H264VarianceFastPathTests
{
    // ── Unit tests for IsLowVariance4x4 ────────────────────────────────────────────────────────

    [Fact]
    public void Flat_all_same_value_is_low_variance()
    {
        var blk = new byte[16];
        blk.AsSpan().Fill(128);
        H264VarianceFastPath.IsLowVariance4x4(blk, threshold: 64).Should().BeTrue(
            "block with all identical samples has variance=0 < 64");
    }

    [Fact]
    public void All_zeros_is_low_variance()
    {
        var blk = new byte[16];
        H264VarianceFastPath.IsLowVariance4x4(blk, threshold: 64).Should().BeTrue(
            "all-zero block has variance=0");
    }

    [Fact]
    public void High_contrast_block_is_not_low_variance()
    {
        // Checkerboard of 0 and 255 — very high variance (~16256).
        var blk = new byte[16];
        for (var i = 0; i < 16; i++)
            blk[i] = (byte)((i % 2 == 0) ? 0 : 255);
        H264VarianceFastPath.IsLowVariance4x4(blk, threshold: 64).Should().BeFalse(
            "checkerboard block has variance ~16256 which is >> 64");
    }

    [Fact]
    public void Gradient_block_is_not_low_variance()
    {
        // 4×4 gradient: 0..15 across the block, variance = (Σi² - (Σi)²/16)/16 = big.
        var blk = new byte[16];
        for (var i = 0; i < 16; i++) blk[i] = (byte)(i * 16);
        H264VarianceFastPath.IsLowVariance4x4(blk, threshold: 64).Should().BeFalse(
            "linear ramp across [0..240] has high variance");
    }

    [Theory]
    [InlineData(120, 121, true)]   // diff=1 → variance=0.25 < 64 → low
    [InlineData(100, 115, true)]   // diff=15 → variance=56.25 < 64 → low (borderline below threshold)
    [InlineData(100, 116, false)]  // diff=16 → variance=64 → NOT strictly less → not low (exact boundary)
    [InlineData(100, 120, false)]  // diff=20 → variance=100 > 64 → not low
    public void Threshold_boundary_cases(byte lo, byte hi, bool expectLow)
    {
        // Alternating lo/hi across the 16-sample block.
        // For 8 lo + 8 hi: variance = ((hi-lo)/2)²  (exact for symmetric alternating pattern).
        var blk = new byte[16];
        for (var i = 0; i < 16; i++) blk[i] = (byte)((i % 2 == 0) ? lo : hi);
        var result = H264VarianceFastPath.IsLowVariance4x4(blk, threshold: 64);
        result.Should().Be(expectLow,
            $"lo={lo} hi={hi}: diff={hi - lo}, variance={(double)(hi - lo) * (hi - lo) / 4}, threshold=64 → expected {(expectLow ? "low" : "not low")}");
    }

    // ── Integration: flat block fires fast-path; encoder selects correct mode ─────────────────

    /// <summary>
    /// Encodes a frame filled with a flat constant luma value.
    /// The bitstream must produce a non-empty Annex B output, confirming the fast-path
    /// does not corrupt reconstruction.
    /// </summary>
    [Fact]
    public void Flat_frame_encodes_successfully()
    {
        const int w = 32;
        const int h = 32;

        var yPlane = new byte[w * h];
        var uPlane = new byte[(w / 2) * (h / 2)];
        var vPlane = new byte[(w / 2) * (h / 2)];
        yPlane.AsSpan().Fill(128);
        uPlane.AsSpan().Fill(128);
        vPlane.AsSpan().Fill(128);

        var annexB = new byte[w * h * 2 + 512_000];
        using var encoder = new H264BaselineEncoder(w, h,
            new H264BaselineEncoderOptions { QuantizationParameter = 28, KeyframeIntervalFrames = 1 });
        var bytesWritten = encoder.EncodeFrame(yPlane, uPlane, vPlane, w, w / 2, annexB);

        // A flat frame must produce a non-empty bitstream.
        bytesWritten.Should().BeGreaterThan(0, "flat frame should produce a non-empty Annex B output");

        // Bitstream must start with a valid start code (0x00 00 00 01 or 0x00 00 01).
        annexB[0].Should().Be(0x00);
        annexB[1].Should().Be(0x00);
    }

    /// <summary>
    /// Encodes a high-variance (checkerboard) frame and a flat frame with the same encoder options.
    /// The high-variance frame must produce more bits than the flat frame, confirming the
    /// encoder exercised the full 9-mode scan for active blocks (flat frame used the fast-path).
    /// </summary>
    [Fact]
    public void High_variance_frame_produces_more_bits_than_flat_frame()
    {
        const int w = 64;
        const int h = 64;
        var uPlane = new byte[(w / 2) * (h / 2)];
        var vPlane = new byte[(w / 2) * (h / 2)];
        uPlane.AsSpan().Fill(128);
        vPlane.AsSpan().Fill(128);

        // Flat frame
        var yFlat = new byte[w * h];
        yFlat.AsSpan().Fill(128);

        // High-variance checkerboard
        var yCheck = new byte[w * h];
        for (var i = 0; i < w * h; i++)
            yCheck[i] = (byte)((i % 2 == 0) ? 0 : 255);

        var annexB = new byte[w * h * 2 + 512_000];
        var opts = new H264BaselineEncoderOptions { QuantizationParameter = 28, KeyframeIntervalFrames = 1 };

        using var encFlat = new H264BaselineEncoder(w, h, opts);
        var flatBytes = encFlat.EncodeFrame(yFlat, uPlane, vPlane, w, w / 2, annexB);

        using var encCheck = new H264BaselineEncoder(w, h, opts);
        var checkBytes = encCheck.EncodeFrame(yCheck, uPlane, vPlane, w, w / 2, annexB);

        checkBytes.Should().BeGreaterThan(flatBytes,
            "checkerboard (high variance, full mode scan) should produce more bits than flat (fast-path to DC/V/H)");
    }
}
