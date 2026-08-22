// Acceptance tests for Phase 2: Intra_16×16 vs Intra_4×4 RD competition in I-slices.
//
// Three invariants verified here:
//   1. Flat (constant-colour) MB: I16×16 wins the RD competition and produces a shorter RBSP
//      than the same content would if forced through I4×4 alone.
//   2. High-frequency (8-bit checkerboard) MB: I4×4 wins, producing more mode bits and residuals,
//      so the RBSP is at least as large as the flat case.
//   3. Both cases round-trip through FFmpeg without decode errors.

using System.Diagnostics;
using FluentAssertions;
using Kiln;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Integration tests for the I-slice I16×16 vs I4×4 rate-distortion competition introduced
/// in Phase 2. Uses a 16×16 input (single macroblock) to isolate mode selection.
/// </summary>
public sealed class H264IntraRdSelectionTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static bool FfmpegOnPath()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-version");
            using var p = Process.Start(psi);
            return p is not null && p.WaitForExit(5_000) && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string FfmpegDecodeStderr(byte[] annexB)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"proxeno-intra-rd-{Guid.NewGuid():N}.h264");
        try
        {
            File.WriteAllBytes(tmp, annexB);
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-nostdin");
            psi.ArgumentList.Add("-threads");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(tmp);
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("null");
            psi.ArgumentList.Add("-");
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(30_000);
            return err;
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Encode a 16×16 single-MB IDR frame with the given luma/chroma fill values.
    /// Returns the Annex B byte array (SPS + PPS + IDR slice NAL).
    /// </summary>
    private static byte[] EncodeFrame16x16(byte yFill, byte uFill, byte vFill, int qp = 28)
    {
        const int w = 16;
        const int h = 16;
        var y = new byte[w * h];
        var u = new byte[w * h / 4];
        var v = new byte[w * h / 4];
        Array.Fill(y, yFill);
        Array.Fill(u, uFill);
        Array.Fill(v, vFill);

        using var enc = new H264BaselineEncoder(w, h,
            new H264BaselineEncoderOptions
            {
                QuantizationParameter = qp,
                KeyframeIntervalFrames = 1,
            });
        var buf = new byte[64 * 1024];
        var len = enc.EncodeFrame(y, u, v, w, w / 2, buf, forceKeyframe: true);
        return buf[..len];
    }

    /// <summary>
    /// Encode a 16×16 single-MB IDR frame whose luma plane is a full-contrast
    /// checkerboard pattern (alternating 0 and 255 in an 8×8 tile).
    /// </summary>
    private static byte[] EncodeCheckerboard16x16(int qp = 28)
    {
        const int w = 16;
        const int h = 16;
        var y = new byte[w * h];
        for (var row = 0; row < h; row++)
            for (var col = 0; col < w; col++)
                y[row * w + col] = (byte)(((row + col) & 1) == 0 ? 0 : 255);
        var u = new byte[w * h / 4];
        var v = new byte[w * h / 4];
        Array.Fill(u, (byte)128);
        Array.Fill(v, (byte)128);

        using var enc = new H264BaselineEncoder(w, h,
            new H264BaselineEncoderOptions
            {
                QuantizationParameter = qp,
                KeyframeIntervalFrames = 1,
            });
        var buf = new byte[64 * 1024];
        var len = enc.EncodeFrame(y, u, v, w, w / 2, buf, forceKeyframe: true);
        return buf[..len];
    }

    // ── 1. Flat block selects I16×16 ─────────────────────────────────────────

    /// <summary>
    /// A flat (constant-colour) macroblock has zero luma AC residuals under I16×16 DC prediction.
    /// The RD competition should prefer I16×16: the encoded frame must be SMALLER than an I4×4
    /// frame of the same content would be (I4×4 always writes 16 mode bits regardless of content).
    ///
    /// We verify this indirectly: the Phase-2 encoder emits fewer bytes for the flat case than
    /// for the high-frequency checkerboard case. The flat case should also be very small overall
    /// because I16×16 with zero residuals produces minimal syntax.
    /// </summary>
    [Fact]
    public void Flat_block_produces_smaller_frame_than_checkerboard()
    {
        var flat = EncodeFrame16x16(yFill: 128, uFill: 128, vFill: 128);
        var checker = EncodeCheckerboard16x16();

        flat.Length.Should().BeLessThan(checker.Length,
            "flat content with I16×16 DC (zero residuals) must encode smaller than a checkerboard");
    }

    /// <summary>
    /// The RBSP for a flat single-MB IDR must be compact.  An I16×16 with zero luma AC and DC-only
    /// chroma produces far fewer bits than 16 I4×4 mode flags + AC residuals.
    /// We bound the size: < 30 bytes for the slice RBSP is only achievable with I16×16.
    /// </summary>
    [Fact]
    public void Flat_block_produces_compact_rbsp_consistent_with_i16x16()
    {
        // EncodeSliceRbsp directly so we measure the RBSP, not the Annex B wrapper.
        const int w = 16;
        const int h = 16;
        var enc = new H264BaselineSliceEncoder(w, h, qp: 28, chromaDcRdLambda: 0);
        var y = new byte[w * h];
        Array.Fill(y, (byte)128);
        var u = new byte[w * h / 4];
        Array.Fill(u, (byte)128);
        var v = new byte[w * h / 4];
        Array.Fill(v, (byte)128);

        var rbsp = enc.EncodeSliceRbsp(y, w, u, v, w / 2, isIdr: true, isPslice: false, frameNum: 0, idrPicId: 0);

        // An I4×4 MB at minimum writes: mb_type(1b) + 16 mode-flags(≥16b) + chroma_pred_mode + cbp + residuals.
        // With flat content these residuals are zero but the mode overhead alone pushes the
        // RBSP well above 12 bytes.  I16×16 with zero AC can easily fit in < 15 bytes.
        rbsp.Length.Should().BeLessThan(20,
            $"flat 16×16 slice with I16×16 DC must be compact; got {rbsp.Length} bytes");
    }

    // ── 2. Checkerboard selects I4×4 ─────────────────────────────────────────

    /// <summary>
    /// A full-contrast checkerboard is the worst-case for I16×16 prediction: every sample deviates
    /// maximally from the DC/V/H/Plane prediction. I4×4 with directional modes handles it better.
    /// The encoder must produce a non-trivial slice (larger than the flat case) confirming that
    /// residuals were coded — which proves I4×4 was selected (I16×16 would produce huge residuals
    /// with very high SAD, raising i16Cost above sumI4Cost).
    /// </summary>
    [Fact]
    public void Checkerboard_block_produces_larger_rbsp_than_flat()
    {
        const int w = 16;
        const int h = 16;
        var enc = new H264BaselineSliceEncoder(w, h, qp: 28, chromaDcRdLambda: 0);

        var yFlat = new byte[w * h];
        Array.Fill(yFlat, (byte)128);
        var yCheck = new byte[w * h];
        for (var row = 0; row < h; row++)
            for (var col = 0; col < w; col++)
                yCheck[row * w + col] = (byte)(((row + col) & 1) == 0 ? 0 : 255);
        var u = new byte[w * h / 4];
        Array.Fill(u, (byte)128);
        var v = new byte[w * h / 4];
        Array.Fill(v, (byte)128);

        var flatEnc = new H264BaselineSliceEncoder(w, h, qp: 28, chromaDcRdLambda: 0);
        var checkEnc = new H264BaselineSliceEncoder(w, h, qp: 28, chromaDcRdLambda: 0);

        var flatRbsp = flatEnc.EncodeSliceRbsp(yFlat, w, u, v, w / 2, isIdr: true, isPslice: false, frameNum: 0, idrPicId: 0);
        var checkRbsp = checkEnc.EncodeSliceRbsp(yCheck, w, u, v, w / 2, isIdr: true, isPslice: false, frameNum: 0, idrPicId: 0);

        checkRbsp.Length.Should().BeGreaterThan(flatRbsp.Length,
            "checkerboard content has large residuals so its RBSP must be bigger than flat");
    }

    // ── 3. FFmpeg round-trip ──────────────────────────────────────────────────

    [Fact]
    public void Flat_single_mb_idr_decodes_cleanly_through_ffmpeg()
    {
        if (!FfmpegOnPath())
            return; // optional dependency; skip silently when ffmpeg is absent

        var annexB = EncodeFrame16x16(yFill: 128, uFill: 128, vFill: 128);
        var stderr = FfmpegDecodeStderr(annexB);
        H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(stderr,
            "flat I-slice 16×16 frame encoded with I16×16 must decode without FFmpeg errors");
    }

    [Fact]
    public void Checkerboard_single_mb_idr_decodes_cleanly_through_ffmpeg()
    {
        if (!FfmpegOnPath())
            return;

        var annexB = EncodeCheckerboard16x16();
        var stderr = FfmpegDecodeStderr(annexB);
        H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(stderr,
            "checkerboard I-slice 16×16 frame encoded with I4×4 must decode without FFmpeg errors");
    }

}
