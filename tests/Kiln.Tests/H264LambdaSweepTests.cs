using System.Diagnostics;
using FluentAssertions;
using Kiln;
using Xunit;
using Xunit.Abstractions;

namespace Kiln.Tests;

/// <summary>
/// Senior-only Phase F harness (NOT delegated to a junior). Sweeps the encoder's intra-4×4 lambda
/// for SAD-based mode selection across a small set of candidate values at representative QPs and
/// reports the (QP, λ) pair that maximises pairwise PSNR vs the libx264 golden. Default-skipped via
/// <see cref="SweepEnabled"/>; flip to <c>true</c> locally when re-tuning after Phase 3 integration
/// widens the mode space (Intra4x4 modes 4..8, Intra16x16, inter ME).
/// </summary>
/// <remarks>
/// Senior toggles by changing <see cref="SweepEnabled"/> and (optionally) the <see cref="LambdaTable"/>.
/// The harness operates on the existing committed I420 fixtures so it never depends on Phase 2
/// junior deliveries to run; it just gives the senior a deterministic spreadsheet of (QP, λ, PSNR).
/// </remarks>
public sealed class H264LambdaSweepTests
{
    /// <summary>Off by default — running this on every CI cycle would re-encode the fixtures dozens of times. Senior flips locally during Phase F lambda tuning.</summary>
    private static readonly bool SweepEnabled =
        Environment.GetEnvironmentVariable("KILN_LAMBDA_SWEEP") == "1";

    /// <summary>
    /// Candidate λ values for SAD + bit-cost RDO. Chosen to span the post-Phase-3 useful range:
    /// the previous table maxed at λ=7 (per the encoder's <c>LambdaSadForQp</c> remarks) so the old
    /// {12, 20, 32} candidates were always saturated; this set covers the dense low end (where the
    /// QP=28 winner sits at λ=2) and trails off through λ=12 to confirm the high-QP optimum.
    /// </summary>
    private static readonly int[] LambdaTable = [0, 1, 2, 3, 4, 5, 6, 8, 12];

    /// <summary>Sweep QPs at typical baseline operating points.</summary>
    private static readonly int[] QpTable = [22, 28, 34];

    /// <summary>Resolutions to sweep — use both committed fixtures so winners reflect both pictures, not just 320×240.</summary>
    private static readonly (int Width, int Height)[] Resolutions = [(320, 240), (256, 224)];

    private readonly ITestOutputHelper _out;

    public H264LambdaSweepTests(ITestOutputHelper output)
    {
        _out = output;
    }

    [Fact]
    public void Sweep_lambda_per_qp_and_log_pairwise_psnr()
    {
        if (!SweepEnabled)
        {
            return;
        }

        if (!TryVerifyFfmpegOnPath())
        {
            return;
        }

        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "H264Golden");

        _out.WriteLine($"# size qp lambda pairwisePSNR(dB) bytes");

        foreach (var (width, height) in Resolutions)
        {
            var i420Path = Path.Combine(fixtureDir, $"frame_{width}x{height}.i420");
            if (!File.Exists(i420Path))
            {
                _out.WriteLine($"# skip {width}x{height}: {i420Path} missing");
                continue;
            }

            var i420 = File.ReadAllBytes(i420Path);
            var ySize = width * height;
            var uvSize = ySize / 4;
            var y = i420.AsSpan(0, ySize).ToArray();
            var u = i420.AsSpan(ySize, uvSize).ToArray();
            var v = i420.AsSpan(ySize + uvSize, uvSize).ToArray();

            foreach (var qp in QpTable)
            {
                foreach (var lambda in LambdaTable)
                {
                    var bytes = EncodeOnce(y, u, v, width, height, qp, lambda);
                    var psnr = MeasurePairwisePsnr(bytes, fixtureDir, width, height, qp);
                    _out.WriteLine($"{width}x{height,-3} {qp,3} {lambda,3} {psnr,7:F2} {bytes.Length,8}");
                    psnr.Should().BeGreaterThan(0,
                        $"sweep {width}x{height} QP {qp} λ {lambda}: pairwise PSNR must be measurable (non-zero) for the harness to be useful.");
                }
            }
        }
    }

    private static byte[] EncodeOnce(
        ReadOnlySpan<byte> y, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v,
        int width, int height, int qp, int lambda)
    {
        var opts = new H264BaselineEncoderOptions
        {
            QuantizationParameter = qp,
            KeyframeIntervalFrames = 60,
            PreferHardwareIntrinsics = true,
            Intra4x4SadLambda = lambda,
        };

        var annex = new byte[width * height * 2 + 512_000];
        using var enc = new H264BaselineEncoder(width, height, opts);
        var n = enc.EncodeFrame(y, u, v, width, width / 2, annex, forceKeyframe: false);
        return annex.AsSpan(0, n).ToArray();
    }

    private static double MeasurePairwisePsnr(byte[] annex, string fixtureDir, int width, int height, int qp)
    {
        var goldenPath = Path.Combine(fixtureDir, $"golden_libx264_{width}x{height}_qp{qp}_idr.h264");
        if (!File.Exists(goldenPath))
        {
            return -1;
        }

        var tmp = Path.Combine(Path.GetTempPath(), $"proxeno-sweep-{Guid.NewGuid():N}.h264");
        try
        {
            File.WriteAllBytes(tmp, annex);
            var ours = DecodeYuv420(tmp, width, height);
            var golden = DecodeYuv420(goldenPath, width, height);
            if (ours.Length != golden.Length || ours.Length == 0)
            {
                return -1;
            }

            double sumSq = 0;
            for (var i = 0; i < ours.Length; i++)
            {
                var d = ours[i] - golden[i];
                sumSq += d * d;
            }

            var mse = sumSq / ours.Length;
            return mse <= 1e-12 ? 99.0 : 10.0 * Math.Log10(255.0 * 255.0 / mse);
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

    private static byte[] DecodeYuv420(string h264, int w, int h)
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
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("h264");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(h264);
        psi.ArgumentList.Add("-frames:v");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("yuv420p");
        psi.ArgumentList.Add("-");

        using var p = Process.Start(psi);
        if (p is null)
        {
            return [];
        }

        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        _ = p.StandardError.ReadToEnd();
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

            return [];
        }

        return p.ExitCode == 0 ? ms.ToArray() : [];
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
            return p?.WaitForExit(10_000) == true && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
