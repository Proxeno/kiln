using System;
using System.Diagnostics;
using System.Security.Cryptography;
using Kiln;
using Kiln.RateControl;

namespace Kiln.Benchmarks;

/// <summary>
/// Stopwatch probe for the slice-dispatch scheduler. Two measurements:
/// <list type="bullet">
/// <item><c>dispatch</c>: per-frame dispatch+join wall time for 2, 4 and 8 slices over a tiny,
/// flat 64×128 picture (1 macroblock row per slice at 8 slices). The slice bodies are near-empty,
/// so this number is dominated by queueing, wakeups, join and the single-threaded orchestration
/// fence around them.</item>
/// <item><c>encode</c>: a real 1280×720 <see cref="EncoderSpeedMode.VeryFast"/> P-frame sequence at
/// 4 and 8 slices, reporting ms/frame and the SHA-256 of the emitted Annex B stream so two builds
/// can be compared byte for byte.</item>
/// </list>
/// Not a BenchmarkDotNet run: for absolute numbers, run it on an idle system. Otherwise treat the
/// output as evidence for a before/after pairing and alternate runs of the two builds rather than
/// comparing single runs.
/// </summary>
internal static class H264SchedulerProbe
{
    private const int DispatchWidth = 64;
    private const int DispatchHeight = 128; // 8 macroblock rows: 1 row per slice at 8 slices.
    private static readonly int[] DispatchSliceCounts = [2, 4, 8];
    private static readonly int[] EncodeSliceCounts = [4, 8];
    private const int DispatchWarmupIters = 400;
    private const int DispatchRounds = 7;
    private const int DispatchItersPerRound = 500;
    private const int EncodeRounds = 5;
    private const int EncodeFramesPerRound = 30;

    public static void Run()
    {
        Console.WriteLine($"// sched-probe: procCount={Environment.ProcessorCount} runtime={Environment.Version}");
        Console.WriteLine("dispatch: per-frame dispatch+join over a near-empty 64x128 picture (us/frame)");
        foreach (var slices in DispatchSliceCounts)
        {
            RunDispatch(slices);
        }

        Console.WriteLine("encode: 1280x720 VeryFast P-frame ms/frame + SHA-256 of the timed Annex B stream");
        foreach (var slices in EncodeSliceCounts)
        {
            RunEncode(1280, 720, slices);
        }
    }

    private static void RunDispatch(int slices)
    {
        var ys = DispatchWidth * DispatchHeight;
        var uv = ys / 4;
        var flat = new byte[ys + 2 * uv];
        flat.AsSpan().Fill(128);
        var annex = new byte[ys * 4 + 262_144];

        using var enc = new H264BaselineEncoder(DispatchWidth, DispatchHeight, new H264BaselineEncoderOptions
        {
            QuantizationParameter = 28,
            KeyframeIntervalFrames = int.MaxValue,
            SpeedMode = EncoderSpeedMode.VeryFast,
            SliceCount = slices,
        });

        var y = flat.AsSpan(0, ys);
        var u = flat.AsSpan(ys, uv);
        var v = flat.AsSpan(ys + uv, uv);

        _ = enc.EncodeFrame(y, u, v, DispatchWidth, DispatchWidth / 2, annex, forceKeyframe: true);
        for (var i = 0; i < 4; i++)
        {
            _ = enc.EncodeFrame(y, u, v, DispatchWidth, DispatchWidth / 2, annex);
        }

        // Warm the JIT, the shared worker pool and the OS wakeup path before timing anything.
        for (var i = 0; i < DispatchWarmupIters; i++)
        {
            _ = enc.EncodeFrame(y, u, v, DispatchWidth, DispatchWidth / 2, annex);
        }

        var roundUs = new double[DispatchRounds];
        for (var r = 0; r < DispatchRounds; r++)
        {
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < DispatchItersPerRound; i++)
            {
                _ = enc.EncodeFrame(y, u, v, DispatchWidth, DispatchWidth / 2, annex);
            }

            sw.Stop();
            roundUs[r] = sw.Elapsed.TotalMilliseconds * 1000.0 / DispatchItersPerRound;
        }

        Console.WriteLine(
            $"  slices={slices}  rounds(us/frame)=[{string.Join(", ", Array.ConvertAll(roundUs, x => x.ToString("F2")))}]" +
            $"  min={Min(roundUs):F2} median={Median(roundUs):F2}");
    }

    private static void RunEncode(int w, int h, int slices)
    {
        var frames = H264ResolutionSliceSweepBenchmarks.GenerateFrames(w, h);
        var ys = w * h;
        var uv = ys / 4;
        var annex = new byte[ys * 2 + 1_048_576];

        using var enc = new H264BaselineEncoder(w, h, new H264BaselineEncoderOptions
        {
            QuantizationParameter = 28,
            KeyframeIntervalFrames = int.MaxValue,
            LevelIdc = 40,
            SpeedMode = EncoderSpeedMode.VeryFast,
            SliceCount = slices,
        });

        var frameIndex = 0;
        int EncodeOne(bool keyframe)
        {
            var f = frames[frameIndex % H264ResolutionSliceSweepBenchmarks.FrameCycle];
            frameIndex++;
            return enc.EncodeFrame(
                f.AsSpan(0, ys), f.AsSpan(ys, uv), f.AsSpan(ys + uv, uv), w, w / 2, annex, keyframe);
        }

        _ = EncodeOne(keyframe: true);
        for (var i = 0; i < 4; i++)
        {
            _ = EncodeOne(keyframe: false);
        }

        for (var i = 0; i < EncodeFramesPerRound; i++)
        {
            _ = EncodeOne(keyframe: false);
        }

        var roundMs = new double[EncodeRounds];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var r = 0; r < EncodeRounds; r++)
        {
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < EncodeFramesPerRound; i++)
            {
                var n = EncodeOne(keyframe: false);
                hash.AppendData(annex, 0, n);
            }

            sw.Stop();
            roundMs[r] = sw.Elapsed.TotalMilliseconds / EncodeFramesPerRound;
        }

        var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        Console.WriteLine(
            $"  {w}x{h} slices={slices}  rounds(ms/frame)=[{string.Join(", ", Array.ConvertAll(roundMs, x => x.ToString("F2")))}]" +
            $"  min={Min(roundMs):F2} median={Median(roundMs):F2}  sha256={digest}");
    }

    private static double Min(double[] values)
    {
        var min = double.MaxValue;
        foreach (var v in values)
        {
            if (v < min) min = v;
        }

        return min;
    }

    private static double Median(double[] values)
    {
        var copy = (double[])values.Clone();
        Array.Sort(copy);
        var mid = copy.Length / 2;
        return copy.Length % 2 == 0 ? (copy[mid - 1] + copy[mid]) / 2.0 : copy[mid];
    }
}
