using System.Diagnostics;
using FluentAssertions;
using Kiln;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Regression guard for the P-frame bit-misalignment that surfaces as "mb_skip_run N is invalid"
/// when FFmpeg decodes software-encoded H.264.  See plan:
/// p-frame_mb_skip_run_misalignment_7fbbb7f2.
/// </summary>
public sealed class H264PSliceMbSkipRunReproTests
{
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
            if (p is null) return false;
            p.WaitForExit(10_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    internal static (bool ok, string stderr) RunFfmpegDecode(string path, int frameCount, string loglevel = "error")
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-threads");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add(loglevel);
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("h264");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(path);
        psi.ArgumentList.Add("-frames:v");
        psi.ArgumentList.Add(frameCount.ToString());
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("null");
        psi.ArgumentList.Add("-");
        using var proc = Process.Start(psi)!;
        proc.WaitForExit(60_000);
        return (proc.ExitCode == 0, proc.StandardError.ReadToEnd());
    }

    /// <summary>
    /// Generates SubpelPanningStripeColumns I420 directly (no RGB pipeline):
    /// dark=28 / bright=235 in luma, neutral chroma.
    /// Matches the spirit of H264VtParityFidelityTests.FillSubpelPanningStripeColumns.
    /// </summary>
    internal static (byte[] Y, byte[] U, byte[] V) BuildSubpelStripeFrame(int w, int h, int frameIndex)
    {
        const byte dark = 28;
        const byte bright = 235;
        var y = new byte[w * h];
        var uvW = w / 2;
        var uvH = h / 2;
        var u = new byte[uvW * uvH];
        var v = new byte[uvW * uvH];
        for (var row = 0; row < h; row++)
        {
            for (var col = 0; col < w; col++)
            {
                var band = (((col + frameIndex * 11) >> 4) & 1) == 0;
                y[row * w + col] = band ? dark : bright;
            }
        }
        Array.Fill(u, (byte)128);
        Array.Fill(v, (byte)128);
        return (y, u, v);
    }

    internal static byte[] EncodeSubpelStripesAnnexB(int w, int h, int frameCount, int qp, int keyInterval)
    {
        using var enc = new H264BaselineEncoder(w, h, new H264BaselineEncoderOptions
        {
            QuantizationParameter = qp,
            KeyframeIntervalFrames = keyInterval,
            LightweightDeblocking = false,
            PreferRealtimeLatencyTuning = false,
        });
        var cap = w * h * 2 + 1_000_000;
        var buf = new byte[cap * frameCount];
        var pos = 0;
        for (var i = 0; i < frameCount; i++)
        {
            var (y, u, v) = BuildSubpelStripeFrame(w, h, i);
            pos += enc.EncodeFrame(y, u, v, w, w / 2, buf.AsSpan(pos));
        }
        return buf[..pos];
    }

    /// <summary>
    /// Confirms the P-frame misalignment reproduces in isolation from VideoToolbox.
    /// This test is expected to FAIL (mb_skip_run present in stderr) on a buggy encoder
    /// and PASS (no mb_skip_run) once the fix is applied.
    /// </summary>
    [Fact]
    public void SubpelStripes_P_slice_decodes_without_mb_skip_run_error_when_ffmpeg_available()
    {
        if (!TryVerifyFfmpegOnPath()) return;

        // 50 frames needed: the bug manifests at specific stripe phases
        // (frame 17, 33, 49 all have phase 11 mod 16) — 2-frame encode misses them.
        const int w = 256, h = 224, qp = 28, keyInterval = 30;
        var annex = EncodeSubpelStripesAnnexB(w, h, frameCount: 50, qp, keyInterval);

        var tmp = Path.Combine(Path.GetTempPath(), $"proxeno-h264-mbskiprun-{Guid.NewGuid():N}.h264");
        try
        {
            File.WriteAllBytes(tmp, annex);
            var (_, stderr) = RunFfmpegDecode(tmp, frameCount: 50);
            stderr.Should().NotContain("mb_skip_run",
                because: "the P-slice bitstream must not misalign (mb_skip_run must be ≤ _mbCount)");
            stderr.Should().NotContain("error while decoding",
                because: "all MBs in the P-slice must parse cleanly");
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
        }
    }
}
