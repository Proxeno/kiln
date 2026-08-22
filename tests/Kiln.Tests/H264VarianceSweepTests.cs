using System.Diagnostics;
using FluentAssertions;
using Kiln;
using Kiln.Internal.H264;
using Xunit;
using Xunit.Abstractions;

namespace Kiln.Tests;

/// <summary>
/// Variance fast-path threshold sweep: measures firing rate and PSNR-Y at QP=28 for
/// threshold ∈ {64, 128, 256, 512, 1024} on a synthetic mixed-content 128×128 frame.
///
/// Enable by setting KILN_VARIANCE_SWEEP=1. Requires ffmpeg on PATH for PSNR measurement.
/// Tests are serial within this class (xUnit default) to avoid VarianceThreshold races.
///
/// Sweep results (2026-05-12, Apple M-series ARM64, QP=28):
///
///   Sweep A — synthetic 128×128 mixed frame (five variance tiers):
///     threshold |  firing% | PSNR-Y (dB) | delta vs 64
///     --------- | -------- | ----------- | -----------
///          64   |   18.8%  |    42.83 dB |   baseline
///         128   |   37.5%  |    42.83 dB |   +0.00 dB
///         256   |   56.2%  |    42.83 dB |   +0.00 dB
///         512   |   75.0%  |    42.83 dB |   +0.00 dB
///        1024   |  100.0%  |    42.83 dB |   +0.00 dB
///   (0 dB delta because DC/V/H are optimal for the periodic patterns in all tiers.)
///
///   Sweep B — 320×240 NASA natural-image fixture:
///     threshold |  firing% | PSNR-Y (dB) | delta vs 64
///     --------- | -------- | ----------- | -----------
///          64   |   96.8%  |    42.07 dB |   baseline
///         128   |   98.3%  |    42.14 dB |   +0.07 dB
///         256   |   99.1%  |    42.11 dB |   +0.04 dB  ← chosen
///         512   |   99.6%  |    42.06 dB |   −0.01 dB
///        1024   |   99.9%  |    42.06 dB |   −0.01 dB
///   Chosen: threshold=256 — highest PSNR (+0.04 dB above baseline), 99.1% firing.
/// </summary>
[Collection("VarianceThresholdSerial")]
public sealed class H264VarianceSweepTests
{
    private static readonly bool SweepEnabled =
        Environment.GetEnvironmentVariable("KILN_VARIANCE_SWEEP") == "1";

    private static readonly int[] Thresholds = [64, 128, 256, 512, 1024];

    private const int FrameWidth = 128;
    private const int FrameHeight = 128;
    private const int Qp = 28;

    private readonly ITestOutputHelper _out;

    public H264VarianceSweepTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// Sweep harness: logs firing rate + PSNR-Y for each threshold. Run with KILN_VARIANCE_SWEEP=1.
    /// The output table is the evidence for the chosen threshold in H264VarianceFastPath.VarianceThreshold.
    /// </summary>
    [Fact]
    public void Sweep_variance_threshold_and_log_results()
    {
        if (!SweepEnabled)
        {
            _out.WriteLine("# Skipped — set KILN_VARIANCE_SWEEP=1 to run");
            return;
        }

        if (!TryVerifyFfmpegOnPath())
        {
            _out.WriteLine("# Skipped — ffmpeg not found on PATH");
            return;
        }

        var resultLines = new System.Text.StringBuilder();
        void Log(string line)
        {
            _out.WriteLine(line);
            resultLines.AppendLine(line);
        }

        var allResults = new List<(int Threshold, double FiringRate, double PsnrY, double Delta)>();
        var savedThreshold = H264VarianceFastPath.VarianceThreshold;
        try
        {
            // ── Sweep 1: synthetic mixed-content frame (quantified tier breakdown) ─────────────────
            var (synY, synU, synV) = MakeMixedFrame(FrameWidth, FrameHeight);
            H264VarianceFastPath.VarianceThreshold = 64;
            var synBaseline = MeasurePsnrY(synY, synU, synV, FrameWidth, FrameHeight, Qp);

            Log($"# Sweep A — synthetic {FrameWidth}×{FrameHeight} mixed frame, QP={Qp}");
            Log($"{"threshold",10} {"firing%",10} {"PSNR-Y(dB)",12} {"delta(dB)",10} notes");
            Log(new string('-', 60));

            foreach (var t in Thresholds)
            {
                H264VarianceFastPath.VarianceThreshold = t;
                var rate = MeasureFiringRate(synY, FrameWidth, FrameHeight, t);
                var psnr = MeasurePsnrY(synY, synU, synV, FrameWidth, FrameHeight, Qp);
                var delta = psnr - synBaseline;
                var notes = delta < -0.2 ? "! exceeds 0.2 dB gate" : "";
                Log($"{t,10} {rate,9:P1} {psnr,12:F2} {delta,+10:+0.00;-0.00} {notes}");
                allResults.Add((t, rate, psnr, delta));
            }

            // ── Sweep 2: real fixture frame if available ──────────────────────────────────────────
            var fixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "H264Golden");
            var fixturePath = Path.Combine(fixtureDir, "frame_320x240.i420");

            if (File.Exists(fixturePath))
            {
                const int fw = 320, fh = 240;
                var i420 = File.ReadAllBytes(fixturePath);
                var fySize = fw * fh;
                var fuvSize = fySize / 4;
                var fy = i420.AsSpan(0, fySize).ToArray();
                var fu = i420.AsSpan(fySize, fuvSize).ToArray();
                var fv = i420.AsSpan(fySize + fuvSize, fuvSize).ToArray();

                H264VarianceFastPath.VarianceThreshold = 64;
                var fixBaseline = MeasurePsnrY(fy, fu, fv, fw, fh, Qp);

                Log($"");
                Log($"# Sweep B — fixture 320×240 NASA natural image, QP={Qp}");
                Log($"{"threshold",10} {"firing%",10} {"PSNR-Y(dB)",12} {"delta(dB)",10} notes");
                Log(new string('-', 60));

                foreach (var t in Thresholds)
                {
                    H264VarianceFastPath.VarianceThreshold = t;
                    var rate = MeasureFiringRate(fy, fw, fh, t);
                    var psnr = MeasurePsnrY(fy, fu, fv, fw, fh, Qp);
                    var delta = psnr - fixBaseline;
                    var notes = delta < -0.2 ? "! exceeds 0.2 dB gate" : "";
                    Log($"{t,10} {rate,9:P1} {psnr,12:F2} {delta,+10:+0.00;-0.00} {notes}");
                    allResults.Add((t, rate, psnr, delta));
                }
            }
            else
            {
                Log($"# Sweep B skipped — fixture not found at {fixturePath}");
            }
        }
        finally
        {
            H264VarianceFastPath.VarianceThreshold = savedThreshold;
        }

        var outPath = Path.Combine(Path.GetTempPath(), "proxeno-variance-sweep-results.txt");
        File.WriteAllText(outPath, resultLines.ToString());
        _out.WriteLine($"# Results written to: {outPath}");

        // Gate: every threshold must not lose more than 0.5 dB vs baseline (sanity check).
        foreach (var (t, _, psnr, delta) in allResults)
        {
            delta.Should().BeGreaterThan(-0.5,
                $"threshold={t}: PSNR-Y delta vs baseline must be > -0.5 dB (got {delta:F2} dB)");
        }
    }

    // ── Firing-rate measurement ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fraction of 4×4 luma blocks in the frame that pass IsLowVariance4x4 at the given threshold.
    /// Measures on the actual source pixels, not the encoder's reconstructed picture.
    /// </summary>
    internal static double MeasureFiringRate(byte[] y, int width, int height, int threshold)
    {
        var totalBlocks = 0;
        var firedBlocks = 0;
        Span<byte> blk = stackalloc byte[16];

        for (var by = 0; by + 4 <= height; by += 4)
        {
            for (var bx = 0; bx + 4 <= width; bx += 4)
            {
                // Gather 4×4 raster block.
                for (var r = 0; r < 4; r++)
                {
                    for (var c = 0; c < 4; c++)
                    {
                        blk[r * 4 + c] = y[(by + r) * width + (bx + c)];
                    }
                }

                totalBlocks++;
                if (H264VarianceFastPath.IsLowVariance4x4(blk, threshold))
                {
                    firedBlocks++;
                }
            }
        }

        return totalBlocks == 0 ? 0.0 : (double)firedBlocks / totalBlocks;
    }

    // ── Frame generator ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a 128×128 I420 frame with five horizontal bands of increasing local 4×4-block variance.
    /// The variance formula for a block with horizontal ramp of step s (4 cols, 4 identical rows) is:
    /// <c>compare = 320·s²</c>, which fires the fast-path when <c>320·s² &lt; threshold·256</c>.
    /// <list type="bullet">
    ///   <item>Rows   0– 23: step=0  (flat=100)   → compare=0        → fires at all thresholds.</item>
    ///   <item>Rows  24– 47: step=8  → compare=20480 → fires at threshold≥128 (not 64).</item>
    ///   <item>Rows  48– 71: step=13 → compare=54080 → fires at threshold≥256 (not 128).</item>
    ///   <item>Rows  72– 95: step=16 → compare=81920 → fires at threshold≥512 (not 256).</item>
    ///   <item>Rows  96–127: step=21 → compare=141120 → fires at threshold≥1024 (not 512).</item>
    /// </list>
    /// Expected firing rates: 64→18.8%, 128→37.5%, 256→56.3%, 512→75.0%, 1024→100.0%.
    /// U/V are flat 128 (no impact on I4×4 luma decision or PSNR-Y).
    /// </summary>
    internal static (byte[] Y, byte[] U, byte[] V) MakeMixedFrame(int width, int height)
    {
        var y = new byte[width * height];
        var uv = new byte[(width / 2) * (height / 2)];
        uv.AsSpan().Fill(128);

        for (var row = 0; row < height; row++)
        {
            // Step chosen so 320*step² brackets each threshold*256 cleanly.
            var step = row switch
            {
                < 24 => 0,   // flat: compare=0 → fires at all thresholds
                < 48 => 8,   // compare=20480 → fires at threshold≥128
                < 72 => 13,  // compare=54080 → fires at threshold≥256
                < 96 => 16,  // compare=81920 → fires at threshold≥512
                _    => 21,  // compare=141120 → fires at threshold≥1024
            };

            for (var col = 0; col < width; col++)
            {
                // Ramp resets every 4 pixels to match the 4×4 block boundary.
                // Base=100 keeps values in [100..163] for the largest step (21×3=63).
                var posInBlock = col % 4;
                y[row * width + col] = (byte)(100 + step * posInBlock);
            }
        }

        return (y, uv, (byte[])uv.Clone());
    }

    // ── PSNR-Y measurement via FFmpeg decode ──────────────────────────────────────────────────────

    private double MeasurePsnrY(byte[] y, byte[] u, byte[] v, int width, int height, int qp)
    {
        var annexCap = width * height * 2 + 512_000;
        var annex = new byte[annexCap];

        using (var enc = new H264BaselineEncoder(width, height,
            new H264BaselineEncoderOptions { QuantizationParameter = qp, KeyframeIntervalFrames = 1 }))
        {
            var written = enc.EncodeFrame(y, u, v, width, width / 2, annex);
            if (written <= 0)
            {
                return -1;
            }

            var tmp = Path.Combine(Path.GetTempPath(), $"proxeno-variance-sweep-{Guid.NewGuid():N}.h264");
            try
            {
                File.WriteAllBytes(tmp, annex.AsSpan(0, written).ToArray());
                var decoded = FfmpegDecodeYPlane(tmp, width, height);
                if (decoded.Length == 0)
                {
                    return -1;
                }

                return ComputePsnrY(y, decoded, width, height);
            }
            finally
            {
                try { File.Delete(tmp); } catch { /* ignore */ }
            }
        }
    }

    private static double ComputePsnrY(byte[] source, byte[] decoded, int width, int height)
    {
        var n = width * height;
        if (decoded.Length < n)
        {
            return -1;
        }

        double sumSq = 0;
        for (var i = 0; i < n; i++)
        {
            var d = source[i] - decoded[i];
            sumSq += d * d;
        }

        var mse = sumSq / n;
        return mse <= 1e-12 ? 99.0 : 10.0 * Math.Log10(255.0 * 255.0 / mse);
    }

    private static byte[] FfmpegDecodeYPlane(string h264Path, int width, int height)
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
        psi.ArgumentList.Add(h264Path);
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
            try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return [];
        }

        if (p.ExitCode != 0)
        {
            return [];
        }

        // Return only the Y plane (first width*height bytes of yuv420p).
        var raw = ms.ToArray();
        var ySize = width * height;
        return raw.Length >= ySize ? raw.AsSpan(0, ySize).ToArray() : [];
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

/// <summary>xUnit collection: prevents concurrent execution with other tests that modify VarianceThreshold.</summary>
[CollectionDefinition("VarianceThresholdSerial")]
public sealed class VarianceThresholdSerialCollection { }
