using System.Diagnostics;
using Kiln;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Encoder↔decoder parity when the intra-in-P fallback fires next to sub-partitioned inter
/// macroblocks. Guards a latent MV-predictor bug: an intra neighbour (refIdx −1, present) was
/// conflated with a truly-absent neighbour (PART_NOT_AVAILABLE), so the encoder fired the C←D
/// substitution / B&amp;C inheritance rule (H.264 §8.4.1.3.1) where the decoder did not — producing
/// a different motion-vector predictor and a sprite predicted from a shifted location (visible
/// "tearing"). The content below (a sharp checkerboard sprite scrolling fast over a parallax
/// background) reliably drives both intra-in-P (the sprite's leading edge / occlusion) and
/// neighbouring sub-partitioned inter MBs, so any predictor divergence shows as a collapse in
/// per-frame encoder-recon-vs-decoded PSNR.
/// </summary>
public sealed class H264IntraInPMvpParityTests
{
    private const int W = 256;
    private const int H = 192;

    private static void FillFrame(byte[] y, byte[] u, byte[] v, int f)
    {
        var uvW = W / 2;
        var uvH = H / 2;
        for (var row = 0; row < H; row++)
            for (var col = 0; col < W; col++)
            {
                var far = col + f * 6; // parallax background scroll
                y[row * W + col] = (byte)(40 + ((far * 5 + row * 9) & 63) + (((far >> 4) ^ (row >> 4)) & 1) * 30);
            }

        var sx = (W - 60) - f * 22; // sharp sprite moving left fast → poor inter match → intra-in-P
        for (var r = 0; r < 48; r++)
            for (var c = 0; c < 48; c++)
            {
                var xx = sx + c;
                var yy = H / 2 + r;
                if (xx < 0 || xx >= W || yy < 0 || yy >= H) continue;
                y[yy * W + xx] = (byte)(((r / 6 + c / 6) & 1) == 0 ? 235 : 20);
            }

        // Reveal strip: from frame 1 a smooth gradient appears where frame 0 held textured background.
        // Smooth content predicts cheaply with Intra_16×16 (V/plane) but has no good inter match against
        // the textured reference → forces I_16×16-in-P. Static thereafter (skips on later frames). The
        // strip's intra MBs become the top-right (C) neighbour of inter MBs to their lower-left, which is
        // exactly the configuration that mishandles the median MVP (C intra wrongly substituted by D).
        if (f >= 1)
        {
            for (var row = 32; row < H - 32; row++)
                for (var col = 88; col < 136; col++)
                    y[row * W + col] = (byte)(70 + ((col - 88) & 31) * 3 + (row & 1) * 2);
        }

        for (var row = 0; row < uvH; row++)
            for (var col = 0; col < uvW; col++)
            {
                u[row * uvW + col] = (byte)(110 + (((col + f * 9) + row) & 31));
                v[row * uvW + col] = (byte)(150 - (((col + f * 9) - row) & 31));
            }
    }

    private static double Psnr(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int n)
    {
        double sse = 0;
        for (var i = 0; i < n; i++) { double d = a[i] - b[i]; sse += d * d; }
        return sse == 0 ? 99.0 : 10 * Math.Log10(255.0 * 255.0 * n / sse);
    }

    [Fact]
    public void Intra_in_P_beside_inter_macroblocks_stays_decoder_parity()
    {
        if (!TryVerifyFfmpegOnPath()) return; // ffmpeg not on PATH — skip gracefully

        const int frames = 8;
        var ySize = W * H;
        var uvSize = ySize / 4;
        var frameBytes = ySize + 2 * uvSize;

        var srcY = new byte[frames][];
        var encReconY = new byte[frames][];
        var annexB = new byte[ySize * 3 + 2_000_000];
        var total = 0;

        H264PInterDiagnostics.ResetPhaseCounts();
        H264PInterDiagnostics.CollectPhaseCounts = true;
        try
        {
            using var enc = new H264BaselineEncoder(W, H, new H264BaselineEncoderOptions
            {
                QuantizationParameter = 24,
                KeyframeIntervalFrames = 300,
                PreferRealtimeLatencyTuning = true,
            });
            for (var f = 0; f < frames; f++)
            {
                var y = new byte[ySize];
                var u = new byte[uvSize];
                var v = new byte[uvSize];
                FillFrame(y, u, v, f);
                srcY[f] = y;
                total += enc.EncodeFrame(y, u, v, W, W / 2, annexB.AsSpan(total), forceKeyframe: f == 0);
                encReconY[f] = enc.LastReconstructedY.ToArray();
            }
        }
        finally
        {
            H264PInterDiagnostics.CollectPhaseCounts = false;
        }

        var (_, _, intraWin) = H264PInterDiagnostics.ReadPhaseCounts();
        Assert.True(intraWin > 0,
            "test content did not exercise the intra-in-P fallback — it cannot guard the MVP bug");

        var (ok, stderr, raw) = FfmpegDecodeRawYuv420MultiFrame(annexB.AsSpan(0, total).ToArray(), W, H, frames);
        Assert.True(ok, $"ffmpeg decode failed: {stderr}");

        // The encoder's own reconstruction and the decoder's output must agree for a conformant
        // bitstream. An MV-predictor divergence shows up as a large per-MB mismatch (a shifted
        // prediction), collapsing this PSNR well below the deblock-rounding floor.
        for (var f = 1; f < frames; f++) // P frames only
        {
            Assert.True(raw.Length >= (f + 1) * frameBytes, $"decoder produced too few frames at {f}");
            var dec = raw.AsSpan(f * frameBytes, ySize);
            var parity = Psnr(encReconY[f], dec, ySize);
            Assert.True(parity > 45.0,
                $"frame {f}: encoder-recon vs decoded PSNR {parity:F2} dB — MV predictor diverged " +
                "(intra-in-P neighbour mishandled in the median MVP).");
        }
    }

    // ── ffmpeg helpers (self-contained per project convention) ──────────────────────────────────────

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

    private static (bool ok, string stderr, byte[] raw) FfmpegDecodeRawYuv420MultiFrame(byte[] annexB, int w, int h, int frameCount)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"proxeno-mvp-par-{Guid.NewGuid():N}.h264");
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
            return (p.ExitCode == 0, err, ms.ToArray());
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }
}
