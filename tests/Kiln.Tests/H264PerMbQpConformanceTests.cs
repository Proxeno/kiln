using System.Diagnostics;
using FluentAssertions;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Guards the two deblocking conformance bugs that per-MB QP streams exposed (found while wiring
/// the rate-control feedback loop; both verified against ffmpeg <em>and</em> VideoToolbox, which
/// agreed with each other byte-exactly against the encoder's reconstruction):
/// <list type="number">
/// <item>The luma filter used the current MB's QP for MB-boundary edges instead of the §8.7.2.2
/// average <c>qPav = (QPp + QPq + 1) &gt;&gt; 1</c>. Invisible with constant QP (the average of
/// equals is the value), wrong for every QP-varying stream.</item>
/// <item><c>H264InterBoundaryStrength.ComputeBs</c> tested the MV/ref condition (bS=1) before the
/// coded-coefficient condition (bS=2), inverting §8.7.2.1's precedence. Masked wherever Table 8-17
/// gives equal tC0 for bS 1 and 2 — which covers QP 23/28/33/34, the QPs the byte-exact oracle
/// tests happened to use — but silently desynchronising at e.g. QP 31/32/35/36, constant QP
/// included.</item>
/// </list>
/// Both desyncs compound through the DPB frame over frame — the same failure class as the v0.2.0
/// P_Skip MV-derivation bug — so these tests assert byte-exact luma reconstruction parity against
/// ffmpeg, not PSNR.
/// </summary>
public sealed class H264PerMbQpConformanceTests
{
    private const int W = 320;
    private const int H = 240;
    private const int Frames = 6;

    /// <summary>Constant-QP guard for the bS-precedence fix: QP 31 and 35 sit in Table 8-17 rows
    /// where tC0 differs between bS 1 and 2, so a precedence inversion is visible; QP 28 sits in a
    /// masked row and pins that the fix did not disturb the masked case.</summary>
    [Theory]
    [InlineData(28)]
    [InlineData(31)]
    [InlineData(35)]
    public void Constant_qp_recon_is_byte_exact_vs_ffmpeg(int qp)
    {
        AssertReconMatchesFfmpeg(new H264BaselineEncoderOptions
        {
            QuantizationParameter = qp,
            KeyframeIntervalFrames = 1000,
        });
    }

    /// <summary>Per-MB QP via spatial AQ: exercises the §8.7.2.2 qPav averaging on both the
    /// all-intra frame 0 and the P frames.</summary>
    [Fact]
    public void Adaptive_quant_recon_is_byte_exact_vs_ffmpeg()
    {
        AssertReconMatchesFfmpeg(new H264BaselineEncoderOptions
        {
            KeyframeIntervalFrames = 1000,
            AdaptiveQuantStrength = 1.0,
        });
    }

    /// <summary>Per-MB QP via the per-frame bit budget (single- and multi-slice): the rate
    /// controller's mb_qp_delta chain must deblock identically to a conformant decoder.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Rate_controlled_recon_is_byte_exact_vs_ffmpeg(int slices)
    {
        AssertReconMatchesFfmpeg(new H264BaselineEncoderOptions
        {
            KeyframeIntervalFrames = 1000,
            TargetBitsPerFrame = 30_000,
            SliceCount = slices,
        });
    }

    private static void AssertReconMatchesFfmpeg(H264BaselineEncoderOptions options)
    {
        if (!TryVerifyFfmpegOnPath())
        {
            return;
        }

        var frames = GenerateScrollingContent(W, H, Frames);
        var ys = W * H;
        var uv = ys / 4;
        var stream = new MemoryStream();
        var reconPerFrame = new byte[Frames][];

        using (var enc = new H264BaselineEncoder(W, H, options))
        {
            var annex = new byte[enc.RecommendedOutputBufferSize];
            for (var i = 0; i < Frames; i++)
            {
                var f = frames[i];
                var n = enc.EncodeFrame(f.AsSpan(0, ys), f.AsSpan(ys, uv), f.AsSpan(ys + uv, uv), W, W / 2, annex);
                stream.Write(annex, 0, n);
                reconPerFrame[i] = enc.LastReconstructedY[..ys].ToArray();
            }
        }

        var decoded = FfmpegDecodeAllFrames(stream.ToArray());
        var frameBytes = ys + 2 * uv;
        decoded.Length.Should().BeGreaterThanOrEqualTo(Frames * frameBytes, "ffmpeg must decode all frames");
        for (var i = 0; i < Frames; i++)
        {
            decoded.AsSpan(i * frameBytes, ys).SequenceEqual(reconPerFrame[i]).Should().BeTrue(
                $"frame {i}: encoder luma reconstruction must match the reference decoder byte-exactly " +
                "(a deblocking QP/bS mismatch compounds through the DPB into visible drift)");
        }
    }

    private static byte[] FfmpegDecodeAllFrames(byte[] annexB)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"kiln-permbqp-{Guid.NewGuid():N}.264");
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
            H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(err, "the stream must decode without errors");
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

    /// <summary>Globally scrolling texture plus a moving bright square: every P frame carries
    /// residual next to real motion, the constellation where bS precedence and edge-QP averaging
    /// both matter.</summary>
    private static byte[][] GenerateScrollingContent(int w, int h, int count)
    {
        var ys = w * h;
        var uv = ys / 4;
        var frames = new byte[count][];
        for (var f = 0; f < count; f++)
        {
            var buf = new byte[ys + 2 * uv];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    buf[y * w + x] = (byte)((x * 3 + y * 5 + f * 9 + (((x + f * 6) / 7) * ((y + f * 4) / 5) % 31)) & 0xFF);
                }
            }

            var side = Math.Min(64, Math.Min(w, h) / 2);
            var bx = (f * 18) % Math.Max(1, w - side);
            var by = (f * 11) % Math.Max(1, h - side);
            for (var yy = 0; yy < side; yy++)
            {
                buf.AsSpan((by + yy) * w + bx, side).Fill(235);
            }

            buf.AsSpan(ys, uv).Fill(120);
            buf.AsSpan(ys + uv, uv).Fill(130);
            frames[f] = buf;
        }

        return frames;
    }
}
