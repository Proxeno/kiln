using System.Diagnostics;
using FluentAssertions;
using Kiln;

namespace Kiln.Tests;

public sealed class H264TrellisFrameQualityTests
{
    private const int W = 64;
    private const int H = 64;

    private static void FillI420Gradient(byte[] y, byte[] u, byte[] v)
    {
        for (var row = 0; row < H; row++)
        {
            for (var col = 0; col < W; col++)
            {
                y[row * W + col] = (byte)((row * 13 + col * 17) & 0xFF);
            }
        }

        var cw = W / 2;
        var ch = H / 2;
        for (var row = 0; row < ch; row++)
        {
            for (var col = 0; col < cw; col++)
            {
                u[row * cw + col] = (byte)(96 + ((row ^ col) & 0x3F));
                v[row * cw + col] = (byte)(144 - ((row + col) & 0x3F));
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
            if (p is null)
            {
                return false;
            }

            _ = p.WaitForExit(10_000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDecodeWithFfmpeg(ReadOnlySpan<byte> annexBytes, out string stderr)
    {
        stderr = "";
        var tmp = Path.Combine(Path.GetTempPath(), $"proxeno-trellis-ff-{Guid.NewGuid():N}.h264");
        try
        {
            File.WriteAllBytes(tmp, annexBytes.ToArray());
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
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("h264");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(tmp);
            psi.ArgumentList.Add("-frames:v");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("null");
            psi.ArgumentList.Add("-");

            using var p = Process.Start(psi);
            if (p is null)
            {
                return false;
            }

            stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(60_000))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignore
                }

                return false;
            }

            return p.ExitCode == 0;
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static byte[] EncodeOnce(int trellisLevel)
    {
        var y = new byte[W * H];
        var u = new byte[W * H / 4];
        var v = new byte[W * H / 4];
        FillI420Gradient(y, u, v);

        var opts = new H264BaselineEncoderOptions
        {
            QuantizationParameter = 22,
            KeyframeIntervalFrames = 1,
            PreferHardwareIntrinsics = true,
            TrellisLevel = trellisLevel,
        };

        var annex = new byte[W * H * 2 + 512_000];
        using var enc = new H264BaselineEncoder(W, H, opts);
        var n = enc.EncodeFrame(y, u, v, W, W / 2, annex, forceKeyframe: false);
        return annex.AsSpan(0, n).ToArray();
    }

    [Fact]
    public void TrellisLevel_1_produces_different_bytes_than_trellis_off_for_gradient_frame()
    {
        var a = EncodeOnce(0);
        var b = EncodeOnce(1);
        a.AsSpan().SequenceEqual(b).Should().BeFalse("trellis should change at least one coefficient on a non-flat input");
    }

    [Fact]
    public void TrellisLevel_1_stream_decodes_cleanly_with_ffmpeg_when_available()
    {
        if (!TryVerifyFfmpegOnPath())
        {
            return;
        }

        var bytes = EncodeOnce(1);
        var ok = TryDecodeWithFfmpeg(bytes, out var stderr);
        Assert.True(ok, $"ffmpeg decode failed. stderr:{Environment.NewLine}{stderr}");
        H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(stderr, "trellis-on Annex B must decode without libav errors");
    }
}
