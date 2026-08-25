using System.Diagnostics;
using FluentAssertions;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Guards encoder/decoder reconstruction parity on sustained-motion content with a two-reference
/// DPB. The P_Skip MV derivation historically forced (0,0) when the A or B neighbour had committed
/// refIdx&gt;0 — a condition H.264 8.4.1.1 does not list — so the encoder's reconstruction silently
/// diverged from a conformant decoder's wherever the (0,0) and spec-predicted blocks happened to be
/// pixel-identical (flat regions), and the decoder's MV field then carried the spec value into every
/// later MVP/P_Skip derivation touching the MB, compounding into dB-scale drift on motion content.
/// This test asserts the encoder's internal luma reconstruction is byte-exact against ffmpeg's
/// decode for every frame, at both single-slice and multi-slice configurations.
/// </summary>
public sealed class H264EncoderFfmpegReconDriftTests
{
    private const int W = 640;
    private const int H = 480;
    private const int Frames = 6;

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Encoder_recon_matches_ffmpeg_decode_on_high_motion_two_ref_content(int slices)
    {
        if (!TryVerifyFfmpegOnPath())
        {
            return;
        }

        var frames = GenerateHighMotion(W, H);
        var ys = W * H;
        var uv = ys / 4;
        var annex = new byte[ys * 2 + 1_048_576];
        var stream = new MemoryStream();
        var reconPerFrame = new byte[Frames][];

        using (var enc = new Kiln.H264BaselineEncoder(W, H, new Kiln.H264BaselineEncoderOptions
               {
                   QuantizationParameter = 23,
                   KeyframeIntervalFrames = int.MaxValue,
                   LevelIdc = 40,
                   SliceCount = slices,
               }))
        {
            for (var i = 0; i < Frames; i++)
            {
                var f = frames[i % frames.Length];
                var n = enc.EncodeFrame(
                    f.AsSpan(0, ys), f.AsSpan(ys, uv), f.AsSpan(ys + uv, uv), W, W / 2, annex, forceKeyframe: i == 0);
                stream.Write(annex, 0, n);
                reconPerFrame[i] = enc.LastReconstructedY[..ys].ToArray();
            }
        }

        var decoded = FfmpegDecodeAllFrames(stream.ToArray());
        decoded.Length.Should().BeGreaterThanOrEqualTo(Frames * (ys + 2 * uv), "ffmpeg must decode all frames");

        var frameBytes = ys + 2 * uv;
        for (var i = 0; i < Frames; i++)
        {
            var dec = decoded.AsSpan(i * frameBytes, ys);
            var badPixels = 0;
            for (var p = 0; p < ys; p++)
            {
                if (dec[p] != reconPerFrame[i][p])
                {
                    badPixels++;
                }
            }

            badPixels.Should().Be(0,
                $"frame {i} (slices={slices}): encoder luma reconstruction must be byte-exact against " +
                $"the reference decoder — any divergence compounds through the DPB into quality drift");
        }
    }

    private static byte[] FfmpegDecodeAllFrames(byte[] annexB)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"kiln-recon-drift-{Guid.NewGuid():N}.264");
        var outYuv = tmp + ".yuv";
        try
        {
            File.WriteAllBytes(tmp, annexB);
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", tmp, "-f", "rawvideo", "-pix_fmt", "yuv420p", outYuv })
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            p.ExitCode.Should().Be(0, $"ffmpeg decode must succeed; stderr: {err}");
            return File.ReadAllBytes(outYuv);
        }
        finally
        {
            File.Delete(tmp);
            if (File.Exists(outYuv))
            {
                File.Delete(outYuv);
            }
        }
    }

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
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Fast diagonal global scroll (12 px/frame) of a textured lattice plus a 26 px/frame square —
    /// enough sustained motion that the two-reference search commits refIdx=1 winners next to
    /// P_Skip candidates (the constellation the historical bug silently mis-derived).
    /// </summary>
    private static byte[][] GenerateHighMotion(int w, int h)
    {
        const int Cycle = 8;
        var ys = w * h;
        var uv = ys / 4;
        var pad = 12 * Cycle;
        var texW = w + pad;
        var texH = h + pad;
        var tex = new byte[texW * texH];
        var rng = new Random(4242);
        var latW = texW / 12 + 2;
        var latH = texH / 12 + 2;
        var lattice = new byte[latW * latH];
        rng.NextBytes(lattice);
        for (var y = 0; y < texH; y++)
        {
            for (var x = 0; x < texW; x++)
            {
                var v = lattice[(y / 12) * latW + x / 12];
                tex[y * texW + x] = (byte)(40 + (v * 170 / 255) + (((x / 6) + (y / 6)) & 1) * 12);
            }
        }

        var frames = new byte[Cycle][];
        for (var f = 0; f < Cycle; f++)
        {
            var frame = new byte[ys + 2 * uv];
            var yPlane = frame.AsSpan(0, ys);
            var shift = f * 12;
            for (var row = 0; row < h; row++)
            {
                tex.AsSpan((row + shift) * texW + shift, w).CopyTo(yPlane.Slice(row * w, w));
            }

            var side = 80;
            var bx = (f * 26) % Math.Max(1, w - side);
            var by = h / 3;
            for (var yy = 0; yy < side; yy++)
            {
                yPlane.Slice((by + yy) * w + bx, side).Fill(240);
            }

            frame.AsSpan(ys, uv).Fill(110);
            frame.AsSpan(ys + uv, uv).Fill(146);
            frames[f] = frame;
        }

        return frames;
    }
}
