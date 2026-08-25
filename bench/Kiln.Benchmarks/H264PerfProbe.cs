using System;
using System.Diagnostics;
using Kiln;
using Kiln.Internal.H264;

namespace Kiln.Benchmarks;

/// <summary>
/// Bounded stopwatch probe for the 1080p P-frame perf investigation. Arms run interleaved
/// chunk-wise in one process so ambient scheduling drift hits every arm equally.
/// Modes: <c>matrix</c> (config arms at 1920x1080 s=4), <c>slices</c> (default config,
/// slice counts 1/2/4/8), <c>spin &lt;arm&gt; &lt;seconds&gt;</c> (continuous encode for
/// external profiler attach; prints PID first).
/// </summary>
internal static class H264PerfProbe
{
    private const int W = 1920;
    private const int H = 1080;

    private sealed record Arm(string Name, bool Satd, int MaxRef, int Slices, bool DisableRef1Margin = false);

    public static void Run(string[] args)
    {
        Console.WriteLine($"// perf-probe: procCount={Environment.ProcessorCount} kernels={H264KernelSet.CreateBest().GetType().Name} pid={Environment.ProcessId}");
        var mode = args.Length > 0 ? args[0] : "matrix";
        switch (mode)
        {
            case "matrix":
                Measure([
                    new Arm("default-s4      ", Satd: true, MaxRef: 2, Slices: 4),
                    new Arm("satdOff-s4      ", Satd: false, MaxRef: 2, Slices: 4),
                    new Arm("ref1-s4         ", Satd: true, MaxRef: 1, Slices: 4),
                    new Arm("satdOff+ref1-s4 ", Satd: false, MaxRef: 1, Slices: 4),
                ]);
                break;
            case "slices":
                Measure([
                    new Arm("default-s1", Satd: true, MaxRef: 2, Slices: 1),
                    new Arm("default-s2", Satd: true, MaxRef: 2, Slices: 2),
                    new Arm("default-s4", Satd: true, MaxRef: 2, Slices: 4),
                    new Arm("default-s8", Satd: true, MaxRef: 2, Slices: 8),
                ]);
                break;
            case "slices-fast":
                Measure([
                    new Arm("fast-s1", Satd: false, MaxRef: 1, Slices: 1),
                    new Arm("fast-s2", Satd: false, MaxRef: 1, Slices: 2),
                    new Arm("fast-s4", Satd: false, MaxRef: 1, Slices: 4),
                    new Arm("fast-s8", Satd: false, MaxRef: 1, Slices: 8),
                ]);
                break;
            case "spin":
            {
                var armName = args.Length > 1 ? args[1] : "default";
                var seconds = args.Length > 2 ? int.Parse(args[2]) : 30;
                var arm = armName switch
                {
                    "default" => new Arm("default", Satd: true, MaxRef: 2, Slices: 4),
                    "satdOff" => new Arm("satdOff", Satd: false, MaxRef: 2, Slices: 4),
                    "ref1" => new Arm("ref1", Satd: true, MaxRef: 1, Slices: 4),
                    "fast" => new Arm("fast", Satd: false, MaxRef: 1, Slices: 4),
                    "default-s1" => new Arm("default-s1", Satd: true, MaxRef: 2, Slices: 1),
                    _ => throw new ArgumentException(armName),
                };
                Spin(arm, seconds);
                break;
            }

            default:
                throw new ArgumentException(mode);
        }
    }

    private static (H264BaselineEncoder Enc, byte[][] Frames, byte[] Annex) Setup(Arm arm)
    {
        var frames = H264ResolutionSliceSweepBenchmarks.GenerateFrames(W, H);
        var annex = new byte[W * H * 2 + 1_048_576];
        var enc = new H264BaselineEncoder(W, H, new H264BaselineEncoderOptions
        {
            QuantizationParameter = 28,
            KeyframeIntervalFrames = int.MaxValue,
            LevelIdc = 40,
            SliceCount = arm.Slices,
            UseMotionSatd = arm.Satd,
            MaxReferenceFrames = arm.MaxRef,
        });
        return (enc, frames, annex);
    }

    private static int Encode(H264BaselineEncoder enc, byte[][] frames, byte[] annex, int index, bool idr)
    {
        const int Ys = W * H;
        const int Uv = Ys / 4;
        var f = frames[index % H264ResolutionSliceSweepBenchmarks.FrameCycle];
        return enc.EncodeFrame(f.AsSpan(0, Ys), f.AsSpan(Ys, Uv), f.AsSpan(Ys + Uv, Uv), W, W / 2, annex, idr);
    }

    private static void Measure(Arm[] arms)
    {
        const int ChunkFrames = 10;
        const int Rounds = 6;
        var encs = new (H264BaselineEncoder Enc, byte[][] Frames, byte[] Annex)[arms.Length];
        var frameIdx = new int[arms.Length];
        var ms = new double[arms.Length][];
        var hex = new long[arms.Length];
        var fb = new long[arms.Length];
        var mbs = new long[arms.Length];
        for (var a = 0; a < arms.Length; a++)
        {
            encs[a] = Setup(arms[a]);
            ms[a] = new double[Rounds];
            H264PInterDiagnostics.DisableRef1TieMargin = arms[a].DisableRef1Margin;
            Encode(encs[a].Enc, encs[a].Frames, encs[a].Annex, frameIdx[a]++, idr: true);
            for (var i = 0; i < 3; i++)
                Encode(encs[a].Enc, encs[a].Frames, encs[a].Annex, frameIdx[a]++, idr: false);
        }

        H264PInterDiagnostics.CollectPhaseCounts = true;
        var sw = new Stopwatch();
        for (var round = 0; round < Rounds; round++)
        {
            for (var a = 0; a < arms.Length; a++)
            {
                H264PInterDiagnostics.DisableRef1TieMargin = arms[a].DisableRef1Margin;
                H264PInterDiagnostics.ResetPhaseCounts();
                sw.Restart();
                for (var i = 0; i < ChunkFrames; i++)
                    Encode(encs[a].Enc, encs[a].Frames, encs[a].Annex, frameIdx[a]++, idr: false);
                sw.Stop();
                ms[a][round] = sw.Elapsed.TotalMilliseconds / ChunkFrames;
                var (hx, f1) = H264PInterDiagnostics.ReadMeSearchCounts();
                hex[a] += hx;
                fb[a] += f1;
                mbs[a] += ChunkFrames;
            }
        }

        H264PInterDiagnostics.CollectPhaseCounts = false;
        H264PInterDiagnostics.DisableRef1TieMargin = false;
        for (var a = 0; a < arms.Length; a++)
        {
            Array.Sort(ms[a]);
            var med = ms[a][ms[a].Length / 2];
            Console.WriteLine(
                $"{arms[a].Name} {med,7:F2} ms/frame (min {ms[a][0]:F2} max {ms[a][^1]:F2})  " +
                $"hex/frame={(double)hex[a] / mbs[a],8:F1} exhaustive/frame={(double)fb[a] / mbs[a],6:F1}");
            encs[a].Enc.Dispose();
        }
    }

    private static void Spin(Arm arm, int seconds)
    {
        var (enc, frames, annex) = Setup(arm);
        var idx = 0;
        Encode(enc, frames, annex, idx++, idr: true);
        for (var i = 0; i < 3; i++)
            Encode(enc, frames, annex, idx++, idr: false);
        Console.WriteLine($"// spin arm={arm.Name} pid={Environment.ProcessId} for {seconds}s");
        var sw = Stopwatch.StartNew();
        var n = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            Encode(enc, frames, annex, idx++, idr: false);
            n++;
        }

        Console.WriteLine($"// spin done: {n} frames, {sw.Elapsed.TotalMilliseconds / n:F2} ms/frame");
        enc.Dispose();
    }
}
