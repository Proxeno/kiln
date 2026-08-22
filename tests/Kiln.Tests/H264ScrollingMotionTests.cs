using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Kiln;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Quality and correctness tests for encoder behaviour on fast-scrolling 2D game content
/// (SNES-style side-scroller: horizontal camera pan, tile-based graphics, static HUD +
/// scrolling world, hard scene cuts). Uses larger frames (64×48, 4×3 MBs) and longer
/// sequences (8–10 frames) than the basic multi-ref tests so that per-frame reconstruction
/// errors compound and become visible — mirroring real playback artefacts.
///
/// Tests that need ffmpeg skip gracefully when it is not on PATH.
/// Strict encoder-decoder parity (byte-exact vs the reference decoder) is opt-in via the
/// KILN_H264_ENCODER_DECODER_PARITY=1 env var to match the rest of the parity suite.
/// PSNR quality gates are unconditional — any tearing/ghosting MB drops PSNR 5–15 dB.
/// </summary>
public sealed class H264ScrollingMotionTests
{
    private const int W = 64;
    private const int H = 48;

    private static bool StrictParityEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("KILN_H264_ENCODER_DECODER_PARITY"),
            "1",
            StringComparison.OrdinalIgnoreCase);

    // ── content generators ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Produces a tile-based I420 frame that simulates a horizontally-scrolling background.
    /// The virtual scroll field is 2× the frame width; each 16×16 block has a distinct
    /// luma/chroma so ME has to find the correct tile position and cannot alias to a wrong tile.
    /// Within-tile spatial variation spreads DCT energy across frequencies so quantisation
    /// artefacts look realistic.
    /// </summary>
    private static void FillScrollFrame(byte[] y, byte[] u, byte[] v, int w, int h, int scrollOffset)
    {
        var vw = w * 2;
        var uvW = w / 2;
        var uvH = h / 2;
        for (var row = 0; row < h; row++)
            for (var col = 0; col < w; col++)
            {
                var vc = ((col + scrollOffset) % vw + vw) % vw;
                var tileCol = vc / 16;
                var tileRow = row / 16;
                // Coefficients are bounded so the maximum (tileCol=7, tileRow=2, full ramps) stays
                // ≤255: 30 + 7*20 + 2*12 + 7*3 + 7*2 = 229. The previous formula overflowed past 255
                // and wrapped (e.g. 257→1), injecting artificial 255→0 cliffs that no codec can
                // predict and that masked real scroll behaviour.
                y[row * w + col] = (byte)(30 + tileCol * 20 + tileRow * 12 + (vc % 8) * 3 + (row % 8) * 2);
            }
        for (var row = 0; row < uvH; row++)
            for (var col = 0; col < uvW; col++)
            {
                var vc = ((col * 2 + scrollOffset) % vw + vw) % vw;
                var tileCol = vc / 16;
                u[row * uvW + col] = (byte)(70 + tileCol * 25);
                v[row * uvW + col] = (byte)(140 - tileCol * 20);
            }
    }

    private static void FillFlat(byte[] y, byte[] u, byte[] v, byte luma, byte chroma)
    {
        Array.Fill(y, luma);
        Array.Fill(u, chroma);
        Array.Fill(v, chroma);
    }

    private static H264BaselineEncoder MakeEncoder(int qp, bool preferHw = false, int kfInterval = 300) =>
        new(W, H, new H264BaselineEncoderOptions
        {
            QuantizationParameter = qp,
            KeyframeIntervalFrames = kfInterval,
            LightweightDeblocking = true,
            PreferRealtimeLatencyTuning = true,
            PreferHardwareIntrinsics = preferHw,
        });

    // ── diagnostic: per-frame PSNR dump ──────────────────────────────────────────────────────────
    // Always-failing harness that dumps per-frame / per-MB PSNR, the encoder MV trace, ffprobe
    // mb_type, and enc-vs-dec pixel rows. Skipped in normal runs; remove the Skip to investigate
    // scroll/scene-cut divergence regressions.

    [Fact(Skip = "diagnostic harness — unskip locally to dump per-frame encoder/decoder divergence")]
    public void Diag_scroll_psnr_per_frame()
    {
        if (!TryVerifyFfmpegOnPath()) return;

        const int Frames = 10;
        const int ScrollPx = 4;
        var ySize = W * H;
        var uvSize = ySize / 4;
        var frameBytes = ySize + 2 * uvSize;

        // Trace all MBs in frames 1 and 2 to see full encoder decisions.
        const int MbW = W / 16;
        const int MbH = H / 16;
        H264PInterDiagnostics.ClearRuntimeTraceMbTargets();
        for (var ty = 0; ty < MbH; ty++)
            for (var tx = 0; tx < MbW; tx++)
            {
                for (var tf = 1; tf <= 6; tf++)
                    H264PInterDiagnostics.AddRuntimeTraceMbTarget(frameNum: tf, tx, ty);
            }
        H264PInterDiagnostics.BuildMbTraceReportAndReset(); // flush any leftover from prior tests

        var sources = new byte[Frames][];
        var encRecons = new byte[Frames][];
        var annex = new byte[ySize * 20 + 512_000];
        var totalBytes = 0;
        var frameSizes = new int[Frames];

        try
        {
            using (var enc = new H264BaselineEncoder(W, H, new H264BaselineEncoderOptions
            {
                QuantizationParameter = 18,
                KeyframeIntervalFrames = 300,
                LightweightDeblocking = true,
                PreferRealtimeLatencyTuning = true,
                SliceCount = 1,
            }))
            {
                for (var f = 0; f < Frames; f++)
                {
                    var y = new byte[ySize]; var u = new byte[uvSize]; var v = new byte[uvSize];
                    FillScrollFrame(y, u, v, W, H, scrollOffset: f * ScrollPx);
                    sources[f] = y;
                    var sz = enc.EncodeFrame(y, u, v, W, W / 2, annex.AsSpan(totalBytes), forceKeyframe: false);
                    frameSizes[f] = sz;
                    totalBytes += sz;
                    encRecons[f] = enc.LastReconstructedY.ToArray();
                }
            }
        }
        finally
        {
            H264PInterDiagnostics.ClearRuntimeTraceMbTargets();
        }

        var mbTrace = H264PInterDiagnostics.BuildMbTraceReportAndReset();

        var annexBytes = annex.AsSpan(0, totalBytes).ToArray();
        var (ok, ffErr, raw) = FfmpegDecodeRawYuv420MultiFrame(annexBytes, W, H, Frames);
        if (!ok) { Assert.True(ok, $"ffmpeg failed: {ffErr}"); return; }
        raw.Length.Should().BeGreaterThanOrEqualTo(Frames * frameBytes);

        // Run ffprobe with -debug mb_type to see decoder's view of each MB's MV and ref.
        var tmpH264 = Path.Combine(Path.GetTempPath(), $"proxeno-diag-{Guid.NewGuid():N}.h264");
        string ffprobeDebug = "";
        try
        {
            File.WriteAllBytes(tmpH264, annexBytes);
            var psi = new System.Diagnostics.ProcessStartInfo("ffprobe",
                $"-i \"{tmpH264}\" -debug mb_type -hide_banner")
            {
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            ffprobeDebug = p.StandardError.ReadToEnd();
            p.WaitForExit();
        }
        catch (Exception ex) { ffprobeDebug = $"ffprobe error: {ex.Message}"; }
        finally { try { File.Delete(tmpH264); } catch { } }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== MB Trace ===");
        sb.AppendLine(mbTrace);
        sb.AppendLine("=== ffprobe -debug mb_type ===");
        sb.AppendLine(ffprobeDebug);
        sb.AppendLine("=== Per-Frame PSNR ===");
        sb.AppendLine("Frame | Size | Overall PSNR | Per-MB PSNR");
        for (var f = 0; f < Frames; f++)
        {
            var decY = raw.AsSpan(f * frameBytes, ySize).ToArray();
            var srcY = sources[f];
            var psnr = ComputePsnr(srcY, decY, ySize);
            sb.Append($"  {f,2} | {frameSizes[f],5} | {psnr:F2} dB  |");
            for (var mby = 0; mby < MbH; mby++)
                for (var mbx = 0; mbx < MbW; mbx++)
                {
                    double mse = 0;
                    for (var r = 0; r < 16; r++)
                        for (var c = 0; c < 16; c++)
                        {
                            var idx = (mby * 16 + r) * W + mbx * 16 + c;
                            var d = (int)srcY[idx] - decY[idx];
                            mse += d * d;
                        }
                    mse /= 256;
                    var mbPsnr = mse < 0.01 ? 99.0 : 10 * Math.Log10(255.0 * 255.0 / mse);
                    sb.Append($" ({mbx},{mby})={mbPsnr:F0}");
                }
            sb.AppendLine();
            if (f == 2 || f == 4 || f == 6)
            {
                var encY = encRecons[f];
                // MB-row boundaries for easy per-MB enc-vs-dec comparison.
                var rowsToShow = new[] { 0, 1, 16, 17, 32, 33 };
                foreach (var row in rowsToShow)
                {
                    sb.Append($"  f{f} row{row:D2} src:");
                    for (var col = 0; col < W; col++) sb.Append($"{srcY[row * W + col],3}");
                    sb.AppendLine();
                    sb.Append($"  f{f} row{row:D2} enc:");
                    for (var col = 0; col < W; col++) sb.Append($"{encY[row * W + col],3}");
                    sb.AppendLine();
                    sb.Append($"  f{f} row{row:D2} dec:");
                    for (var col = 0; col < W; col++) sb.Append($"{decY[row * W + col],3}");
                    sb.AppendLine();
                }
            }
        }
        Assert.Fail(sb.ToString());
    }

    // ── mixed-ref / sub-partition encoder-decoder parity (regression guard) ───────────────────────

    /// <summary>
    /// Fast, non-uniform motion is the case that exposes subtle MV-prediction bugs: parallax + several
    /// independently-moving sprites force neighbouring macroblocks (and 8×8 sub-partitions within an
    /// MB) onto different motion vectors and different reference indices, and drive sub-MB partitioning
    /// (P_16×8 / P_8×16 / P_8×8) right up against the picture edges where neighbour availability and the
    /// §8.4.1.3.1 C←D substitution matter. Uniform global scroll never reaches these combinations.
    ///
    /// For every frame the encoder's own reconstruction must be byte-identical to the reference decoder's output.
    /// Any MVP / skip / sub-partition divergence (the encoder writing an MVD relative to a predictor the
    /// decoder derives differently) shows up here as a non-zero per-MB delta — exactly the class of
    /// hard-to-find drift that uniform-motion tests miss. Runs across several frame geometries so the
    /// failing column/row lands at different edges. Skipped when ffmpeg is absent.
    /// </summary>
    [Theory]
    [InlineData(128, 96)]   // 8×6 MBs
    [InlineData(112, 80)]   // 7×5 MBs — different right/bottom edge alignment
    [InlineData(96, 112)]   // 6×7 MBs — taller than wide
    public void Fast_motion_mixed_ref_parity(int fw, int fh)
    {
        if (!TryVerifyFfmpegOnPath()) return;

        const int frames = 14;
        var ySize = fw * fh;
        var uvSize = ySize / 4;
        var frameBytes = ySize + 2 * uvSize;

        var sources = new byte[frames][];
        var reconY = new byte[frames][];
        var reconU = new byte[frames][];
        var reconV = new byte[frames][];
        var annex = new byte[ySize * 20 + 1_000_000];
        var totalBytes = 0;

        using (var enc = new H264BaselineEncoder(fw, fh, new H264BaselineEncoderOptions
        {
            QuantizationParameter = 22,
            KeyframeIntervalFrames = 300,
            LightweightDeblocking = true,
            PreferRealtimeLatencyTuning = true,
            SliceCount = 1,
        }))
        {
            for (var f = 0; f < frames; f++)
            {
                var y = new byte[ySize]; var u = new byte[uvSize]; var v = new byte[uvSize];
                FillParallaxSpriteField(y, u, v, fw, fh, f);
                sources[f] = y;
                totalBytes += enc.EncodeFrame(y, u, v, fw, fw / 2, annex.AsSpan(totalBytes), forceKeyframe: false);
                reconY[f] = enc.LastReconstructedY.ToArray();
                reconU[f] = enc.LastReconstructedU.ToArray();
                reconV[f] = enc.LastReconstructedV.ToArray();
            }
        }

        var (ok, ffErr, raw) = FfmpegDecodeRawYuv420MultiFrame(annex.AsSpan(0, totalBytes).ToArray(), fw, fh, frames);
        Assert.True(ok, $"ffmpeg decode failed. stderr:{Environment.NewLine}{ffErr}");
        H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(ffErr, "fast-motion mixed-ref decode");
        raw.Length.Should().BeGreaterThanOrEqualTo(frames * frameBytes);

        for (var f = 0; f < frames; f++)
        {
            var dec = raw.AsSpan(f * frameBytes, frameBytes);
            var div = FindFirstDivergence(
                reconY[f], reconU[f], reconV[f],
                dec[..ySize], dec[ySize..(ySize + uvSize)], dec[(ySize + uvSize)..],
                fw, fh, frameIndex: f);
            if (div is not null)
                Assert.Fail(
                    $"Encoder reconstruction != reference-decoder output at {fw}×{fh}, frame {f} — a motion-vector " +
                    $"predictor / sub-partition / skip divergence (commonly a mixed-reference or picture-edge " +
                    $"neighbour case).\n{div.DetailText}");
        }
    }

    // Scrolling textured background + several small sprites moving on independent fast vectors, so
    // neighbouring MBs (and 8×8 sub-partitions within an MB) get different motion — the case that
    // uniform global scroll never exercises. Coordinates are deterministic per frame.
    private static void FillParallaxSpriteField(byte[] y, byte[] u, byte[] v, int w, int h, int f)
    {
        var uvW = w / 2; var uvH = h / 2;
        var bgDx = f * 6; // background pans 6 px/frame
        for (var row = 0; row < h; row++)
            for (var col = 0; col < w; col++)
            {
                var sx = col + bgDx; var sy = row;
                var t = (sx * 5 + sy * 9) & 63;
                y[row * w + col] = (byte)(48 + t + ((sx >> 3) & 1) * 20);
            }
        // Sprites: (startX, startY, vx, vy, size, luma). Fast, independent, some > 8 px/frame.
        (int x0, int y0, int vx, int vy, int sz, int lum)[] sprites =
        {
            (10, 20, 13, 3, 18, 200),
            (90, 60, -11, -5, 14, 30),
            (50, 10, 7, 9, 12, 240),
        };
        foreach (var (x0, y0, vx, vy, sz, lum) in sprites)
        {
            var px = ((x0 + vx * f) % w + w) % w;
            var py = ((y0 + vy * f) % h + h) % h;
            for (var r = 0; r < sz; r++)
                for (var c = 0; c < sz; c++)
                {
                    var yy = py + r; var xx = px + c;
                    if (yy >= h || xx >= w) continue;
                    y[yy * w + xx] = (byte)((lum + (r ^ c) * 6) & 0xFF);
                }
        }
        for (var row = 0; row < uvH; row++)
            for (var col = 0; col < uvW; col++)
            {
                var sx = col * 2 + bgDx;
                u[row * uvW + col] = (byte)(110 + ((sx + row * 2) & 31));
                v[row * uvW + col] = (byte)(150 - ((sx - row * 2) & 31));
            }
    }

    // ── diagnostic: fade to/from black ────────────────────────────────────────────────────────────
    [Fact(Skip = "diagnostic harness — unskip locally to probe fade-to-black blocking")]
    public void Diag_fade_to_black()
    {
        if (!TryVerifyFfmpegOnPath()) return;

        const int fw = 128, fh = 96, frames = 16;
        var ySize = fw * fh; var uvSize = ySize / 4; var frameBytes = ySize + 2 * uvSize;
        var sources = new byte[frames][];
        var annex = new byte[ySize * 20 + 1_000_000];
        var totalBytes = 0;
        var sizes = new int[frames];

        H264PInterDiagnostics.CollectPhaseCounts = true;
        using (var enc = new H264BaselineEncoder(fw, fh, new H264BaselineEncoderOptions
        {
            QuantizationParameter = 22,
            KeyframeIntervalFrames = 300,
            LightweightDeblocking = true,
            PreferRealtimeLatencyTuning = true,
            SliceCount = 1,
        }))
        {
            for (var f = 0; f < frames; f++)
            {
                var y = new byte[ySize]; var u = new byte[uvSize]; var v = new byte[uvSize];
                FillFadeFrame(y, u, v, fw, fh, f, frames);
                sources[f] = y;
                sizes[f] = enc.EncodeFrame(y, u, v, fw, fw / 2, annex.AsSpan(totalBytes), forceKeyframe: false);
                totalBytes += sizes[f];
            }
        }
        H264PInterDiagnostics.CollectPhaseCounts = false;

        var (ok, ffErr, raw) = FfmpegDecodeRawYuv420MultiFrame(annex.AsSpan(0, totalBytes).ToArray(), fw, fh, frames);
        if (!ok) { Assert.True(ok, $"ffmpeg failed: {ffErr}"); return; }

        var mbW = fw / 16; var mbH = fh / 16;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Frame | fade% | size | PSNR | worst MBs (psnr<34)");
        for (var f = 0; f < frames; f++)
        {
            var decY = raw.AsSpan(f * frameBytes, ySize).ToArray();
            var psnr = ComputePsnr(sources[f], decY, ySize);
            var worst = new System.Text.StringBuilder();
            for (var mby = 0; mby < mbH; mby++)
                for (var mbx = 0; mbx < mbW; mbx++)
                {
                    double mse = 0;
                    for (var r = 0; r < 16; r++)
                        for (var c = 0; c < 16; c++)
                        { var i = (mby * 16 + r) * fw + mbx * 16 + c; var d = sources[f][i] - decY[i]; mse += d * d; }
                    mse /= 256;
                    var p = mse < 0.01 ? 99.0 : 10 * Math.Log10(255.0 * 255.0 / mse);
                    if (p < 34) worst.Append($" ({mbx},{mby})={p:F0}");
                }
            var fadePct = (f <= frames / 2 ? f : frames - f) * 100 / (frames / 2);
            sb.AppendLine($"  {f,2} | {fadePct,3}% | {sizes[f],5} | {psnr,6:F2} dB |{worst}");
        }
        Assert.Fail(sb.ToString());
    }

    // ── fast-motion-onset quality (search-range / local-minimum regression guard) ─────────────────

    /// <summary>
    /// An abrupt jump from static to fast (24 px/frame) scroll — a camera that suddenly starts
    /// panning, the staple of a 2D runner. No spatial or temporal predictor points anywhere near the
    /// true motion, so the fast (hex) motion search can lodge in a local minimum and miss it entirely,
    /// leaving every fast frame ~20 dB blocky until it crawls back. The exhaustive fallback (taken when
    /// the fast search returns a clearly poor 16×16 match) must recover this: every fast frame should
    /// stay well above the tearing/blocking floor. Skipped when ffmpeg is absent.
    /// </summary>
    [Fact]
    public void Fast_motion_onset_psnr_does_not_collapse()
    {
        if (!TryVerifyFfmpegOnPath()) return;

        const int fw = 128, fh = 96;
        // Static, then an abrupt jump to fast steady 24 px/frame scroll, then stop.
        int[] offsets = [0, 0, 0, 0, 24, 48, 72, 96, 120, 144, 144, 144];
        var frames = offsets.Length;
        var ySize = fw * fh; var uvSize = ySize / 4; var frameBytes = ySize + 2 * uvSize;
        var sources = new byte[frames][];
        var annex = new byte[ySize * 20 + 1_000_000];
        var totalBytes = 0;

        using (var enc = new H264BaselineEncoder(fw, fh, new H264BaselineEncoderOptions
        {
            QuantizationParameter = 22,
            KeyframeIntervalFrames = 300,
            LightweightDeblocking = true,
            PreferRealtimeLatencyTuning = true,
            SliceCount = 1,
        }))
        {
            for (var f = 0; f < frames; f++)
            {
                var y = new byte[ySize]; var u = new byte[uvSize]; var v = new byte[uvSize];
                FillScrollFrame(y, u, v, fw, fh, offsets[f]);
                sources[f] = y;
                totalBytes += enc.EncodeFrame(y, u, v, fw, fw / 2, annex.AsSpan(totalBytes), forceKeyframe: false);
            }
        }

        var (ok, ffErr, raw) = FfmpegDecodeRawYuv420MultiFrame(annex.AsSpan(0, totalBytes).ToArray(), fw, fh, frames);
        Assert.True(ok, $"ffmpeg decode failed. stderr:{Environment.NewLine}{ffErr}");
        H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(ffErr, "fast-motion-onset decode");

        for (var f = 1; f < frames; f++)
        {
            var decY = raw.AsSpan(f * frameBytes, ySize).ToArray();
            var psnr = ComputePsnr(sources[f], decY, ySize);
            psnr.Should().BeGreaterThan(35.0,
                $"frame {f} (Δscroll {offsets[f] - offsets[f - 1]} px) PSNR must stay above the blocking " +
                $"floor; got {psnr:F1} dB — the fast motion search failed to find the motion (a ~20 dB " +
                $"collapse here means the exhaustive-search fallback for poor hex matches has regressed)");
        }
    }

    // Textured field uniformly scaled by a fade factor: 1.0 → 0 (to black) over the first half, then
    // 0 → 1.0 (from black). Models a palette fade — a global illumination change Baseline H.264 inter
    // prediction cannot model (no weighted prediction), so every MB carries a near-uniform residual.
    private static void FillFadeFrame(byte[] y, byte[] u, byte[] v, int w, int h, int f, int frames)
    {
        var half = frames / 2;
        var fade = f <= half ? 1.0 - (double)f / half : (double)(f - half) / half; // 1→0→1
        var uvW = w / 2; var uvH = h / 2;
        for (var row = 0; row < h; row++)
            for (var col = 0; col < w; col++)
            {
                // Mixed flat + textured regions so skip decisions differ across the frame.
                var tile = (col / 16 + row / 16) & 1;
                var baseLuma = tile == 0 ? 200 : 40 + ((col * 3 + row * 5) & 63);
                y[row * w + col] = (byte)(baseLuma * fade);
            }
        for (var row = 0; row < uvH; row++)
            for (var col = 0; col < uvW; col++)
            {
                u[row * uvW + col] = (byte)(128 + (int)((((col + row) & 31) - 16) * fade));
                v[row * uvW + col] = (byte)(128 + (int)((((col - row) & 31) - 16) * fade));
            }
    }

    // ── test 1: smoke — scrolling bitstream decodes without errors ────────────────────────────────

    /// <summary>
    /// Encodes 10 frames of 4-pixel-per-frame horizontal scroll and verifies that the Annex B
    /// bitstream decodes without errors in ffmpeg. Exercises both scalar and HW-intrinsic
    /// paths. Skipped if ffmpeg is not on PATH.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Horizontal_scroll_decodes_without_errors(bool preferHardwareIntrinsics)
    {
        if (!TryVerifyFfmpegOnPath()) return;

        const int Frames = 10;
        const int ScrollPx = 4;
        var ySize = W * H;
        var uvSize = ySize / 4;
        var annex = new byte[ySize * 20 + 512_000];
        var totalBytes = 0;

        using (var enc = MakeEncoder(qp: 22, preferHw: preferHardwareIntrinsics))
        {
            var y = new byte[ySize]; var u = new byte[uvSize]; var v = new byte[uvSize];
            for (var f = 0; f < Frames; f++)
            {
                FillScrollFrame(y, u, v, W, H, scrollOffset: f * ScrollPx);
                totalBytes += enc.EncodeFrame(y, u, v, W, W / 2, annex.AsSpan(totalBytes), forceKeyframe: false);
            }
        }

        var tmp = Path.Combine(Path.GetTempPath(), $"proxeno-scroll-smoke-{Guid.NewGuid():N}.h264");
        try
        {
            File.WriteAllBytes(tmp, annex.AsSpan(0, totalBytes).ToArray());
            var (ok, ffErr) = RunFfmpegNullSink(tmp, Frames);
            Assert.True(ok, $"ffmpeg failed on horizontal-scroll sequence. stderr:{Environment.NewLine}{ffErr}");
            H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(ffErr, "horizontal scroll smoke");
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    // ── test 2: PSNR quality gate — scrolling frames must not tear ───────────────────────────────

    /// <summary>
    /// Encodes 10 frames of 4 px/frame horizontal scroll at QP=18 and decodes with ffmpeg.
    /// Asserts that every P-frame achieves &gt;38 dB luma PSNR vs source, and that no two
    /// consecutive frames differ by more than 6 dB (a sudden drop indicates a tearing frame).
    /// Any MB that references the wrong DPB slot or has a wrong MV causes a 5–15 dB drop.
    /// </summary>
    [Fact]
    public void Horizontal_scroll_psnr_exceeds_38db_and_no_sudden_drops()
    {
        if (!TryVerifyFfmpegOnPath()) return;

        const int Frames = 10;
        const int ScrollPx = 4;
        var ySize = W * H;
        var uvSize = ySize / 4;
        var frameBytes = ySize + 2 * uvSize;

        var sources = new byte[Frames][];
        var annex = new byte[ySize * 20 + 512_000];
        var totalBytes = 0;

        using (var enc = MakeEncoder(qp: 18))
        {
            for (var f = 0; f < Frames; f++)
            {
                var y = new byte[ySize]; var u = new byte[uvSize]; var v = new byte[uvSize];
                FillScrollFrame(y, u, v, W, H, scrollOffset: f * ScrollPx);
                sources[f] = y;
                totalBytes += enc.EncodeFrame(y, u, v, W, W / 2, annex.AsSpan(totalBytes), forceKeyframe: false);
            }
        }

        var (ok, ffErr, raw) = FfmpegDecodeRawYuv420MultiFrame(annex.AsSpan(0, totalBytes).ToArray(), W, H, Frames);
        Assert.True(ok, $"ffmpeg decode failed. stderr:{Environment.NewLine}{ffErr}");
        H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(ffErr, "scroll PSNR decode");
        raw.Length.Should().BeGreaterThanOrEqualTo(Frames * frameBytes);

        var psnrs = new double[Frames];
        for (var f = 0; f < Frames; f++)
        {
            var decY = raw.AsSpan(f * frameBytes, ySize).ToArray();
            psnrs[f] = ComputePsnr(sources[f], decY, ySize);
        }

        // Skip frame 0 (IDR — always has quantisation error vs source); gate frames 1+.
        for (var f = 1; f < Frames; f++)
        {
            psnrs[f].Should().BeGreaterThan(38.0,
                $"frame {f} luma PSNR must exceed 38 dB at QP=18 on scrolling content; " +
                $"low PSNR indicates tearing or ghosting (psnr={psnrs[f]:F1} dB)");
        }

        for (var f = 2; f < Frames; f++)
        {
            var drop = psnrs[f - 1] - psnrs[f];
            drop.Should().BeLessThan(6.0,
                $"PSNR must not drop more than 6 dB between consecutive frames (frames {f - 1}→{f}); " +
                $"drop={drop:F1} dB indicates a tearing artifact on frame {f}");
        }
    }

    // ── test 3: encoder-decoder parity for scrolling sequence ─────────────────────────────────────

    /// <summary>
    /// Encodes 10 frames of scrolling content and compares the encoder's internal
    /// reconstruction against the ffmpeg-decoded output per MB. The test always verifies that
    /// ffmpeg decodes cleanly; byte-exact parity is opt-in via KILN_H264_ENCODER_DECODER_PARITY=1.
    /// Divergence on any frame means the encoded bitstream does not represent what the encoder
    /// computed, causing subsequent frames to reference the wrong DPB content.
    /// </summary>
    [Fact]
    public void Horizontal_scroll_encoder_decoder_parity()
    {
        if (!TryVerifyFfmpegOnPath()) return;

        const int Frames = 10;
        const int ScrollPx = 4;
        var ySize = W * H;
        var uvSize = ySize / 4;
        var frameBytes = ySize + 2 * uvSize;

        var reconY = new byte[Frames][];
        var reconU = new byte[Frames][];
        var reconV = new byte[Frames][];
        var annex = new byte[ySize * 20 + 512_000];
        var totalBytes = 0;

        using (var enc = MakeEncoder(qp: 20))
        {
            var y = new byte[ySize]; var u = new byte[uvSize]; var v = new byte[uvSize];
            for (var f = 0; f < Frames; f++)
            {
                FillScrollFrame(y, u, v, W, H, scrollOffset: f * ScrollPx);
                totalBytes += enc.EncodeFrame(y, u, v, W, W / 2, annex.AsSpan(totalBytes), forceKeyframe: false);
                reconY[f] = enc.LastReconstructedY.ToArray();
                reconU[f] = enc.LastReconstructedU.ToArray();
                reconV[f] = enc.LastReconstructedV.ToArray();
            }
        }

        var (ok, ffErr, raw) = FfmpegDecodeRawYuv420MultiFrame(annex.AsSpan(0, totalBytes).ToArray(), W, H, Frames);
        Assert.True(ok, $"ffmpeg decode failed. stderr:{Environment.NewLine}{ffErr}");
        H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(ffErr, "scroll parity decode");

        if (!StrictParityEnabled) return;

        raw.Length.Should().BeGreaterThanOrEqualTo(Frames * frameBytes);
        for (var f = 0; f < Frames; f++)
        {
            var dec = raw.AsSpan(f * frameBytes, frameBytes);
            var div = FindFirstDivergence(
                reconY[f], reconU[f], reconV[f],
                dec[..ySize], dec[ySize..(ySize + uvSize)], dec[(ySize + uvSize)..],
                W, H, frameIndex: f);
            if (div is not null)
                Assert.Fail($"Scroll parity: frame {f} encoder reconstruction != ffmpeg decode.\n{div.DetailText}");
        }
    }

    // ── test 4: static HUD + scrolling world ──────────────────────────────────────────────────────

    /// <summary>
    /// Simulates a game layout where the top 16 px (1 MB row) is a flat static HUD and the
    /// bottom 32 px (2 MB rows) scrolls at 4 px/frame. Asserts separate PSNR thresholds for
    /// each region: the static HUD must be near-lossless (&gt;44 dB), the scrolling world
    /// must have acceptable quality (&gt;36 dB). A HUD PSNR failure means static MBs are
    /// being predicted from the wrong reference or MV.
    /// </summary>
    [Fact]
    public void Static_hud_plus_scrolling_world_meets_psnr_thresholds()
    {
        if (!TryVerifyFfmpegOnPath()) return;

        const int Frames = 8;
        const int ScrollPx = 4;
        const int HudRows = 16;
        var ySize = W * H;
        var uvSize = ySize / 4;
        var frameBytes = ySize + 2 * uvSize;

        var sourceY = new byte[Frames][];
        var annex = new byte[ySize * 20 + 512_000];
        var totalBytes = 0;

        using (var enc = MakeEncoder(qp: 20))
        {
            for (var f = 0; f < Frames; f++)
            {
                var y = new byte[ySize]; var u = new byte[uvSize]; var v = new byte[uvSize];
                // Static HUD: flat luma=220, chroma=128 for top 16 rows.
                Array.Fill<byte>(y, 0, 0, W * HudRows);
                for (var r = 0; r < HudRows; r++)
                    for (var c = 0; c < W; c++)
                        y[r * W + c] = 220;
                // Scrolling world: tile pattern in bottom 32 rows.
                var worldY = new byte[W * (H - HudRows)];
                var worldU = new byte[(W / 2) * ((H - HudRows) / 2)];
                var worldV = new byte[(W / 2) * ((H - HudRows) / 2)];
                FillScrollFrame(worldY, worldU, worldV, W, H - HudRows, scrollOffset: f * ScrollPx);
                Array.Copy(worldY, 0, y, W * HudRows, worldY.Length);
                Array.Fill(u, (byte)128);
                Array.Fill(v, (byte)128);
                Array.Copy(worldU, 0, u, (W / 2) * (HudRows / 2), worldU.Length);
                Array.Copy(worldV, 0, v, (W / 2) * (HudRows / 2), worldV.Length);

                sourceY[f] = y[..].ToArray();
                totalBytes += enc.EncodeFrame(y, u, v, W, W / 2, annex.AsSpan(totalBytes), forceKeyframe: false);
            }
        }

        var (ok, ffErr, raw) = FfmpegDecodeRawYuv420MultiFrame(annex.AsSpan(0, totalBytes).ToArray(), W, H, Frames);
        Assert.True(ok, $"ffmpeg decode failed. stderr:{Environment.NewLine}{ffErr}");
        H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(ffErr, "HUD+world parity decode");
        raw.Length.Should().BeGreaterThanOrEqualTo(Frames * frameBytes);

        var hudPixels = W * HudRows;
        var worldPixels = W * (H - HudRows);

        for (var f = 1; f < Frames; f++)
        {
            var decY = raw.AsSpan(f * frameBytes, ySize).ToArray();
            var srcY = sourceY[f];

            var hudPsnr = ComputePsnr(srcY[..hudPixels], decY[..hudPixels], hudPixels);
            hudPsnr.Should().BeGreaterThan(44.0,
                $"frame {f} HUD region (static, luma=220) must have PSNR > 44 dB; " +
                $"got {hudPsnr:F1} dB — HUD corruption or wrong reference");

            var worldSrc = srcY[hudPixels..];
            var worldDec = decY[hudPixels..];
            var worldPsnr = ComputePsnr(worldSrc, worldDec, worldPixels);
            worldPsnr.Should().BeGreaterThan(36.0,
                $"frame {f} world region (scrolling) must have PSNR > 36 dB at QP=20; " +
                $"got {worldPsnr:F1} dB — tearing or ghosting in scrolling region");
        }
    }

    // ── test 5: scene cut recovery ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Encodes a sequence with a hard scene cut mid-stream (no forced IDR): frames 0–3 are
    /// scrolling content A, frame 4 is a flat scene-change frame, frames 5–9 resume scrolling A.
    /// Verifies that the entire sequence decodes without errors and that the encoder recovers
    /// to &gt;34 dB PSNR by frame 9. A recovery failure indicates accumulated DPB errors or
    /// incorrect reference selection after the scene change.
    /// </summary>
    [Fact]
    public void Scene_cut_recovery_stays_decodable_and_recovers_psnr()
    {
        if (!TryVerifyFfmpegOnPath()) return;

        const int Frames = 10;
        const int ScrollPx = 4;
        var ySize = W * H;
        var uvSize = ySize / 4;
        var frameBytes = ySize + 2 * uvSize;

        var sourceY = new byte[Frames][];
        var annex = new byte[ySize * 20 + 512_000];
        var totalBytes = 0;

        using (var enc = MakeEncoder(qp: 22))
        {
            var y = new byte[ySize]; var u = new byte[uvSize]; var v = new byte[uvSize];
            for (var f = 0; f < Frames; f++)
            {
                if (f == 4)
                    FillFlat(y, u, v, luma: 50, chroma: 128); // hard scene cut
                else
                    FillScrollFrame(y, u, v, W, H, scrollOffset: (f < 4 ? f : f - 5) * ScrollPx);
                sourceY[f] = y[..].ToArray();
                totalBytes += enc.EncodeFrame(y, u, v, W, W / 2, annex.AsSpan(totalBytes), forceKeyframe: false);
            }
        }

        var (ok, ffErr, raw) = FfmpegDecodeRawYuv420MultiFrame(annex.AsSpan(0, totalBytes).ToArray(), W, H, Frames);
        Assert.True(ok, $"ffmpeg decode failed after scene cut. stderr:{Environment.NewLine}{ffErr}");
        H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(ffErr, "scene cut decode");
        raw.Length.Should().BeGreaterThanOrEqualTo(Frames * frameBytes);

        // Frame 9 is 5 frames past the scene cut — encoder should have recovered.
        var decY9 = raw.AsSpan(9 * frameBytes, ySize).ToArray();
        var psnr9 = ComputePsnr(sourceY[9], decY9, ySize);
        psnr9.Should().BeGreaterThan(34.0,
            $"frame 9 PSNR must exceed 34 dB after recovering from scene cut at frame 4; " +
            $"got {psnr9:F1} dB — DPB corruption or incorrect reference selection post-cut");
    }

    // ── test 6: tile-period scroll compactness ────────────────────────────────────────────────────

    /// <summary>
    /// Scrolls by exactly one tile width (16 px) per frame, so each P frame sees content
    /// that is tile-period-aligned with DPB slot 0. Most MBs should be P_skip or near-zero
    /// residual. Asserts that P frame sizes are far smaller than the IDR, and that the decoded
    /// output has PSNR &gt;42 dB (near-lossless tile-period scroll).
    /// </summary>
    [Fact]
    public void Tile_period_scroll_produces_compact_p_frames()
    {
        const int Frames = 6;
        const int TileWidthPx = 16;
        var ySize = W * H;
        var uvSize = ySize / 4;
        var annex = new byte[ySize * 12 + 512_000];
        var frameSizes = new int[Frames];
        var totalBytes = 0;

        using (var enc = MakeEncoder(qp: 20))
        {
            var y = new byte[ySize]; var u = new byte[uvSize]; var v = new byte[uvSize];
            for (var f = 0; f < Frames; f++)
            {
                FillScrollFrame(y, u, v, W, H, scrollOffset: f * TileWidthPx);
                frameSizes[f] = enc.EncodeFrame(y, u, v, W, W / 2, annex.AsSpan(totalBytes), forceKeyframe: false);
                totalBytes += frameSizes[f];
            }
        }

        var idrSize = frameSizes[0];
        idrSize.Should().BeGreaterThan(0, "IDR must have non-zero size");

        // Frames 2–5 scroll by exactly one tile, so the interior of the frame is tile-aligned with
        // DPB slot 0 and those macroblocks must collapse to P_skip / zero-residual inter. Only the
        // leading edge (the rightmost column, whose content scrolled in from off-screen and exists in
        // no reference) is irreducible: ~1 of 4 MB columns, coded as Intra_16×16 by the intra-in-P
        // fallback. A healthy P frame therefore stays well below half the IDR; a regression where
        // interior MBs stop skipping pushes the frame size up toward (or past) the IDR. Gate at
        // 0.6·IDR: above the leading-edge floor, far below the interior-fails regression.
        for (var f = 2; f < Frames; f++)
        {
            frameSizes[f].Should().BeLessThan((int)(idrSize * 0.6),
                $"frame {f} (tile-period scroll) must stay well below the IDR ({idrSize} bytes): the " +
                $"interior must collapse to skip/zero-residual and only the leading-edge column may " +
                $"carry new content; got {frameSizes[f]} bytes — interior MBs are not skipping");
        }

        if (!TryVerifyFfmpegOnPath()) return;

        var frameBytes = ySize + 2 * uvSize;
        var (ok, ffErr, raw) = FfmpegDecodeRawYuv420MultiFrame(annex.AsSpan(0, totalBytes).ToArray(), W, H, Frames);
        Assert.True(ok, $"ffmpeg decode failed. stderr:{Environment.NewLine}{ffErr}");
        H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(ffErr, "tile-period scroll decode");

        raw.Length.Should().BeGreaterThanOrEqualTo(Frames * frameBytes);
        var y6 = new byte[ySize]; var u6 = new byte[ySize / 4]; var v6 = new byte[ySize / 4];
        FillScrollFrame(y6, u6, v6, W, H, scrollOffset: 5 * TileWidthPx);
        var decY5 = raw.AsSpan(5 * frameBytes, ySize).ToArray();
        var psnr5 = ComputePsnr(y6, decY5, ySize);
        // 16 px/frame is one whole tile column of off-screen content arriving every frame; the
        // intra-in-P fallback codes that leading edge compactly, trading a few dB there for size
        // (the interior stays near-lossless). 38 dB matches the other scrolling quality gate and is
        // comfortably above any tearing/divergence (which would land far lower and propagate).
        psnr5.Should().BeGreaterThan(38.0,
            $"frame 5 luma PSNR must exceed 38 dB for tile-period scroll at QP=20; " +
            $"got {psnr5:F1} dB");
    }

    // ── PSNR helper ───────────────────────────────────────────────────────────────────────────────

    private static double ComputePsnr(byte[] source, byte[] decoded, int count)
    {
        double sse = 0;
        for (var i = 0; i < count; i++) { var d = source[i] - decoded[i]; sse += d * d; }
        if (sse == 0) return double.PositiveInfinity;
        return 10.0 * Math.Log10(255.0 * 255.0 * count / sse);
    }

    // ── per-MB divergence finder ──────────────────────────────────────────────────────────────────

    private sealed record DivergenceReport(int FrameIndex, int MbX, int MbY, string DetailText);

    private static DivergenceReport? FindFirstDivergence(
        ReadOnlySpan<byte> encY, ReadOnlySpan<byte> encU, ReadOnlySpan<byte> encV,
        ReadOnlySpan<byte> decY, ReadOnlySpan<byte> decU, ReadOnlySpan<byte> decV,
        int w, int h, int frameIndex)
    {
        var uvW = w / 2;
        var mbW = w / 16;
        var mbH = h / 16;
        Span<byte> eL = stackalloc byte[256]; Span<byte> dL = stackalloc byte[256];
        Span<byte> eU = stackalloc byte[64];  Span<byte> dU = stackalloc byte[64];
        Span<byte> eV = stackalloc byte[64];  Span<byte> dV = stackalloc byte[64];
        for (var mby = 0; mby < mbH; mby++)
        {
            for (var mbx = 0; mbx < mbW; mbx++)
            {
                CopyBlock(encY, w, mbx * 16, mby * 16, 16, eL);
                CopyBlock(decY, w, mbx * 16, mby * 16, 16, dL);
                CopyBlock(encU, uvW, mbx * 8, mby * 8, 8, eU);
                CopyBlock(decU, uvW, mbx * 8, mby * 8, 8, dU);
                CopyBlock(encV, uvW, mbx * 8, mby * 8, 8, eV);
                CopyBlock(decV, uvW, mbx * 8, mby * 8, 8, dV);
                var maxL = 0; var maxU = 0; var maxV = 0;
                for (var i = 0; i < 256; i++) maxL = Math.Max(maxL, Math.Abs(eL[i] - dL[i]));
                for (var i = 0; i < 64; i++) { maxU = Math.Max(maxU, Math.Abs(eU[i] - dU[i])); maxV = Math.Max(maxV, Math.Abs(eV[i] - dV[i])); }
                if (maxL == 0 && maxU == 0 && maxV == 0) continue;
                var sb = new StringBuilder();
                sb.AppendLine($"frame={frameIndex} mb=({mbx},{mby}) maxAbs: luma={maxL} U={maxU} V={maxV}");
                sb.AppendLine("Luma 16x16 enc/dec/delta:");
                for (var r = 0; r < 16; r++)
                {
                    sb.Append($"r{r:D2}: ");
                    for (var c = 0; c < 16; c++) { var i = r * 16 + c; sb.Append($"{eL[i]:D3}/{dL[i]:D3}/{(int)eL[i] - dL[i]:+0;-#;0} "); }
                    sb.AppendLine();
                }
                return new DivergenceReport(frameIndex, mbx, mby, sb.ToString());
            }
        }
        return null;
    }

    private static void CopyBlock(ReadOnlySpan<byte> plane, int stride, int ox, int oy, int size, Span<byte> dst)
    {
        for (var r = 0; r < size; r++)
            plane.Slice((oy + r) * stride + ox, size).CopyTo(dst.Slice(r * size, size));
    }

    // ── ffmpeg helpers ────────────────────────────────────────────────────────────────────────────

    private static bool TryVerifyFfmpegOnPath()
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
            return p is not null && p.WaitForExit(10_000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static (bool ok, string stderr) RunFfmpegNullSink(string inputPath, int frameCount)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-hide_banner"); psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-threads"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("h264");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(inputPath);
        psi.ArgumentList.Add("-frames:v"); psi.ArgumentList.Add(frameCount.ToString());
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("null");
        psi.ArgumentList.Add("-");
        using var p = Process.Start(psi);
        if (p is null) return (false, "");
        if (!p.WaitForExit(60_000)) { try { p.Kill(entireProcessTree: true); } catch { } return (false, "timeout"); }
        return (p.ExitCode == 0, p.StandardError.ReadToEnd());
    }

    private static (bool ok, string stderr, byte[] raw) FfmpegDecodeRawYuv420MultiFrame(byte[] annexB, int w, int h, int frameCount)
    {
        var expected = frameCount * w * h * 3 / 2;
        var tmp = Path.Combine(Path.GetTempPath(), $"proxeno-scroll-par-{Guid.NewGuid():N}.h264");
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
            psi.ArgumentList.Add("-hide_banner"); psi.ArgumentList.Add("-nostdin");
            psi.ArgumentList.Add("-threads"); psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("h264");
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(tmp);
            psi.ArgumentList.Add("-frames:v"); psi.ArgumentList.Add(frameCount.ToString());
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("rawvideo");
            psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("yuv420p");
            psi.ArgumentList.Add("-");
            using var p = Process.Start(psi);
            if (p is null) return (false, "process did not start", []);
            using var ms = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(ms);
            var err = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(60_000)) { try { p.Kill(entireProcessTree: true); } catch { } return (false, "timeout", []); }
            var raw = ms.ToArray();
            return p.ExitCode != 0 || raw.Length < expected ? (false, err, raw) : (true, err, raw);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }
}
