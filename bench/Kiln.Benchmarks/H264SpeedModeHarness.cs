using System;
using System.Diagnostics;
using System.IO;
using Kiln;
using Kiln.RateControl;

namespace Kiln.Benchmarks;

/// <summary>
/// Bounded harness for the <see cref="H264BaselineEncoderOptions.SpeedMode"/> ladder.
/// <list type="bullet">
/// <item><c>--speed-modes &lt;outdir&gt;</c>: encodes every content class (static / moving /
/// high-motion / scene-cut / divergent) at QP 23/28/35 for each arm at 640x480, writing raw source
/// + Annex B streams to <c>outdir</c> for external ffmpeg PSNR, printing encoded bytes as it
/// goes.</item>
/// <item><c>--speed-modes-timing</c>: 1920x1080 steady-P ms/frame for each arm on coherent-moving
/// and divergent content, arms interleaved chunk-wise in one process so ambient scheduling drift
/// hits every arm equally.</item>
/// </list>
/// Arms cover the four shipped modes plus tuning variants around the Fast / VeryFast effort caps.
/// </summary>
internal static class H264SpeedModeHarness
{
    private sealed record Arm(string Name, H264BaselineEncoderOptions Options);

    private static Arm[] BuildArms() =>
    [
        new Arm("hq", new H264BaselineEncoderOptions()),
        new Arm("ref1", new H264BaselineEncoderOptions { MaxReferenceFrames = 1 }),
        new Arm("rc8", new H264BaselineEncoderOptions { SubPartitionRangeCap = 8 }),
        new Arm("cap512", new H264BaselineEncoderOptions { MotionSearchEffortCapPerMb = 512 }),
        new Arm("cap1024", new H264BaselineEncoderOptions { MotionSearchEffortCapPerMb = 1024 }),
        new Arm("bal", new H264BaselineEncoderOptions { SpeedMode = EncoderSpeedMode.Balanced }),
        new Arm("bal1024", new H264BaselineEncoderOptions { MaxReferenceFrames = 1, MotionSearchEffortCapPerMb = 1024 }),
        new Arm("fast", new H264BaselineEncoderOptions { SpeedMode = EncoderSpeedMode.Fast }),
        new Arm("fast512", new H264BaselineEncoderOptions { MaxReferenceFrames = 1, SubPartitionRangeCap = 8, MotionSearchEffortCapPerMb = 512 }),
        new Arm("vfast", new H264BaselineEncoderOptions { SpeedMode = EncoderSpeedMode.VeryFast }),
        new Arm("vfast256", new H264BaselineEncoderOptions { MaxReferenceFrames = 1, UseMotionSatd = false, SubPartitionRangeCap = 8, MotionSearchEffortCapPerMb = 256 }),
    ];

    private static (string Name, byte[][] Frames)[] BuildContents(int w, int h) =>
    [
        ("static", H264SliceSweepQuickHarness.GenerateStatic(w, h)),
        ("moving", H264ResolutionSliceSweepBenchmarks.GenerateFrames(w, h)),
        ("highmotion", H264SliceSweepQuickHarness.GenerateHighMotion(w, h)),
        ("scenecut", H264SliceSweepQuickHarness.GenerateSceneCut(w, h)),
        ("divergent", H264PerfProbe.GenerateDivergentFrames(w, h)),
    ];

    /// <summary>Quality dumps: 1920x1080 streams for external decode + PSNR — the resolution the
    /// README quotes, so speed/quality positions pair with the timing runs.</summary>
    public static void RunQuality(string outDir)
    {
        Directory.CreateDirectory(outDir);
        const int W = 1920;
        const int H = 1080;
        const int FrameCount = 24;
        int[] qps = [23, 28, 35];
        Console.WriteLine("content        qp arm      bytes");
        foreach (var (name, frames) in BuildContents(W, H))
        {
            WriteSource(outDir, name, W, H, frames, FrameCount);
            foreach (var qp in qps)
            {
                foreach (var arm in BuildArms())
                {
                    var bytes = EncodeToFile(outDir, name, arm, W, H, frames, FrameCount, qp);
                    Console.WriteLine($"{name,-14} {qp,2} {arm.Name,-8} {bytes}");
                }
            }
        }
    }

    /// <summary>1080p steady-P timing, arms interleaved chunk-wise.</summary>
    public static void RunTiming()
    {
        const int W = 1920;
        const int H = 1080;
        const int ChunkFrames = 8;
        const int Rounds = 7;
        Console.WriteLine($"// speed-modes-timing: procCount={Environment.ProcessorCount} " +
            $"kernels={Internal.H264.H264KernelSet.CreateBest().GetType().Name}");
        Console.WriteLine("content    slices arm      ms/frame(median)  min    max");
        foreach (var (content, frames) in new (string, byte[][])[]
                 {
                     ("moving", H264ResolutionSliceSweepBenchmarks.GenerateFrames(W, H)),
                     ("divergent", H264PerfProbe.GenerateDivergentFrames(W, H)),
                 })
        {
            foreach (var slices in new[] { 1, 4 })
            {
                var arms = BuildArms();
                var ys = W * H;
                var uv = ys / 4;
                var annex = new byte[ys * 2 + 1_048_576];
                var encs = new H264BaselineEncoder[arms.Length];
                var frameIdx = new int[arms.Length];
                var ms = new double[arms.Length][];
                for (var a = 0; a < arms.Length; a++)
                {
                    var opts = arms[a].Options;
                    var run = new H264BaselineEncoderOptions
                    {
                        QuantizationParameter = 28,
                        KeyframeIntervalFrames = int.MaxValue,
                        LevelIdc = 40,
                        SliceCount = slices,
                        SpeedMode = opts.SpeedMode,
                        MaxReferenceFrames = opts.MaxReferenceFrames,
                        UseMotionSatd = opts.UseMotionSatd,
                        SubPartitionRangeCap = opts.SubPartitionRangeCap,
                        MotionSearchEffortCapPerMb = opts.MotionSearchEffortCapPerMb,
                    };
                    encs[a] = new H264BaselineEncoder(W, H, run);
                    ms[a] = new double[Rounds];
                    for (var i = 0; i < 4; i++)
                        Encode(encs[a], frames, annex, W, H, frameIdx[a]++, idr: i == 0);
                }

                var sw = new Stopwatch();
                for (var round = 0; round < Rounds; round++)
                {
                    for (var a = 0; a < arms.Length; a++)
                    {
                        sw.Restart();
                        for (var i = 0; i < ChunkFrames; i++)
                            Encode(encs[a], frames, annex, W, H, frameIdx[a]++, idr: false);
                        sw.Stop();
                        ms[a][round] = sw.Elapsed.TotalMilliseconds / ChunkFrames;
                    }
                }

                for (var a = 0; a < arms.Length; a++)
                {
                    Array.Sort(ms[a]);
                    Console.WriteLine(
                        $"{content,-10} {slices,6} {arms[a].Name,-8} {ms[a][Rounds / 2],7:F2}          {ms[a][0],6:F2} {ms[a][^1],6:F2}");
                    encs[a].Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Prints how hard each effort cap binds per content/QP at 640x480: MBs that reached budget
    /// tier 1/2/3 per frame (of 1200 MBs). Chooses where the preset caps sit on real content.
    /// </summary>
    public static void RunTierDiag()
    {
        const int W = 640;
        const int H = 480;
        const int FrameCount = 24;
        Console.WriteLine("content        qp  cap tier1/2/3 MBs per frame (1200 MBs total)");
        foreach (var (name, frames) in BuildContents(W, H))
        {
            foreach (var qp in new[] { 23, 28, 35 })
            {
                foreach (var cap in new[] { 512, 1024, 2048 })
                {
                    var ys = W * H;
                    var uv = ys / 4;
                    var annex = new byte[ys * 2 + 1_048_576];
                    using var enc = new H264BaselineEncoder(W, H, new H264BaselineEncoderOptions
                    {
                        QuantizationParameter = qp,
                        KeyframeIntervalFrames = int.MaxValue,
                        LevelIdc = 40,
                        SliceCount = 1,
                        MotionSearchEffortCapPerMb = cap,
                    });
                    Internal.H264.H264PInterDiagnostics.CollectPhaseCounts = true;
                    Internal.H264.H264PInterDiagnostics.ResetPhaseCounts();
                    for (var i = 0; i < FrameCount; i++)
                        Encode(enc, frames, annex, W, H, i, idr: i == 0);
                    var (t1, t2, t3) = Internal.H264.H264PInterDiagnostics.ReadMeBudgetTierCounts();
                    Internal.H264.H264PInterDiagnostics.CollectPhaseCounts = false;
                    Console.WriteLine(
                        $"{name,-14} {qp,2} {cap,4} {(double)t1 / FrameCount,7:F0} {(double)t2 / FrameCount,7:F0} {(double)t3 / FrameCount,7:F0}");
                }
            }
        }
    }

    private static void Encode(
        H264BaselineEncoder enc, byte[][] frames, byte[] annex, int w, int h, int index, bool idr)
    {
        var ys = w * h;
        var uv = ys / 4;
        var f = frames[index % frames.Length];
        enc.EncodeFrame(f.AsSpan(0, ys), f.AsSpan(ys, uv), f.AsSpan(ys + uv, uv), w, w / 2, annex, idr);
    }

    private static void WriteSource(string outDir, string name, int w, int h, byte[][] frames, int frameCount)
    {
        using var fs = File.Create(Path.Combine(outDir, $"{name}_{w}x{h}.yuv"));
        for (var i = 0; i < frameCount; i++)
            fs.Write(frames[i % frames.Length]);
    }

    private static long EncodeToFile(
        string outDir, string name, Arm arm, int w, int h, byte[][] frames, int frameCount, int qp)
    {
        var ys = w * h;
        var uv = ys / 4;
        var annex = new byte[ys * 2 + 1_048_576];
        var opts = arm.Options;
        using var enc = new H264BaselineEncoder(w, h, new H264BaselineEncoderOptions
        {
            QuantizationParameter = qp,
            KeyframeIntervalFrames = int.MaxValue,
            LevelIdc = 40,
            SliceCount = 1,
            SpeedMode = opts.SpeedMode,
            MaxReferenceFrames = opts.MaxReferenceFrames,
            UseMotionSatd = opts.UseMotionSatd,
            SubPartitionRangeCap = opts.SubPartitionRangeCap,
            MotionSearchEffortCapPerMb = opts.MotionSearchEffortCapPerMb,
        });
        using var fs = File.Create(Path.Combine(outDir, $"{name}_qp{qp}_{arm.Name}.264"));
        long total = 0;
        for (var i = 0; i < frameCount; i++)
        {
            var f = frames[i % frames.Length];
            var n = enc.EncodeFrame(
                f.AsSpan(0, ys), f.AsSpan(ys, uv), f.AsSpan(ys + uv, uv), w, w / 2, annex, forceKeyframe: i == 0);
            fs.Write(annex.AsSpan(0, n));
            total += n;
        }

        return total;
    }
}
