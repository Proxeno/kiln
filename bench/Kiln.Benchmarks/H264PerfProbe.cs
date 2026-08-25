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

    private sealed record Arm(string Name, bool Satd, int MaxRef, int Slices, bool DisableRef1Margin = false, bool BalanceOff = false, bool QgateOff = false, int RangeCap = 16, int Qp = 28, bool Divergent = false, int? SubPartDivisor = null, int EffortCap = 0);

    private static Arm ResolveArm(string name, int slices) => name switch
    {
        "default" => new Arm($"default-s{slices}", Satd: true, MaxRef: 2, Slices: slices),
        "satdOff" => new Arm($"satdOff-s{slices}", Satd: false, MaxRef: 2, Slices: slices),
        "ref1" => new Arm($"ref1-s{slices}", Satd: true, MaxRef: 1, Slices: slices),
        "fast" => new Arm($"fast-s{slices}", Satd: false, MaxRef: 1, Slices: slices),
        "qgateOff" => new Arm($"qgateOff-s{slices}", Satd: true, MaxRef: 2, Slices: slices, QgateOff: true),
        "rc8" => new Arm($"rc8-s{slices}", Satd: true, MaxRef: 2, Slices: slices, RangeCap: 8),
        "rc4" => new Arm($"rc4-s{slices}", Satd: true, MaxRef: 2, Slices: slices, RangeCap: 4),
        "div" => new Arm($"div-s{slices}", Satd: true, MaxRef: 2, Slices: slices, Divergent: true),
        "rc8div" => new Arm($"rc8div-s{slices}", Satd: true, MaxRef: 2, Slices: slices, RangeCap: 8, Divergent: true),
        "rc4div" => new Arm($"rc4div-s{slices}", Satd: true, MaxRef: 2, Slices: slices, RangeCap: 4, Divergent: true),
        "ref1div" => new Arm($"ref1div-s{slices}", Satd: true, MaxRef: 1, Slices: slices, Divergent: true),
        "satdOffDiv" => new Arm($"satdOffDiv-s{slices}", Satd: false, MaxRef: 2, Slices: slices, Divergent: true),
        "bud64div" => new Arm($"bud64div-s{slices}", Satd: true, MaxRef: 2, Slices: slices, Divergent: true, SubPartDivisor: 64),
        "cap512" => new Arm($"cap512-s{slices}", Satd: true, MaxRef: 2, Slices: slices, EffortCap: 512),
        "cap256" => new Arm($"cap256-s{slices}", Satd: true, MaxRef: 2, Slices: slices, EffortCap: 256),
        "cap128" => new Arm($"cap128-s{slices}", Satd: true, MaxRef: 2, Slices: slices, EffortCap: 128),
        "cap512div" => new Arm($"cap512div-s{slices}", Satd: true, MaxRef: 2, Slices: slices, Divergent: true, EffortCap: 512),
        "cap256div" => new Arm($"cap256div-s{slices}", Satd: true, MaxRef: 2, Slices: slices, Divergent: true, EffortCap: 256),
        "cap128div" => new Arm($"cap128div-s{slices}", Satd: true, MaxRef: 2, Slices: slices, Divergent: true, EffortCap: 128),
        _ => throw new ArgumentException(name),
    };

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
            case "balance":
                Measure([
                    ResolveArm("default", 4), ResolveArm("default", 4) with { Name = "default-s4-eq", BalanceOff = true },
                    ResolveArm("default", 8), ResolveArm("default", 8) with { Name = "default-s8-eq", BalanceOff = true },
                    ResolveArm("fast", 4), ResolveArm("fast", 4) with { Name = "fast-s4-eq   ", BalanceOff = true },
                    ResolveArm("fast", 8), ResolveArm("fast", 8) with { Name = "fast-s8-eq   ", BalanceOff = true },
                ]);
                break;
            case "qgate":
                Measure([
                    ResolveArm("default", 4),
                    ResolveArm("qgateOff", 4),
                    ResolveArm("default", 1),
                    ResolveArm("qgateOff", 1),
                ]);
                break;
            case "rangecap":
                Measure([
                    ResolveArm("default", 4),
                    ResolveArm("rc8", 4),
                    ResolveArm("rc4", 4),
                ]);
                break;
            case "rangecap-div":
                Measure([
                    ResolveArm("div", 4),
                    ResolveArm("rc8div", 4),
                    ResolveArm("rc4div", 4),
                ]);
                break;
            case "budget-div":
                Measure([
                    ResolveArm("div", 4),
                    ResolveArm("cap512div", 4),
                    ResolveArm("cap256div", 4),
                    ResolveArm("cap128div", 4),
                ]);
                break;
            case "budget":
                Measure([
                    ResolveArm("default", 4),
                    ResolveArm("cap512", 4),
                    ResolveArm("cap256", 4),
                    ResolveArm("cap128", 4),
                ]);
                break;
            case "div-parts":
                Measure([
                    ResolveArm("div", 4),
                    ResolveArm("ref1div", 4),
                    ResolveArm("satdOffDiv", 4),
                    ResolveArm("bud64div", 4),
                ]);
                break;
            case "spin":
            {
                var armName = args.Length > 1 ? args[1] : "default";
                var seconds = args.Length > 2 ? int.Parse(args[2]) : 30;
                Spin(ResolveArm(armName, slices: 4), seconds);
                break;
            }

            case "phases":
            case "mb":
            case "satd":
            {
                var armName = args.Length > 1 ? args[1] : "default";
                var slices = args.Length > 2 ? int.Parse(args[2]) : 4;
                var frames = args.Length > 3 ? int.Parse(args[3]) : 200;
                Diagnose(mode, ResolveArm(armName, slices), frames);
                break;
            }

            case "dump":
            {
                var armName = args.Length > 1 ? args[1] : "default";
                var slices = args.Length > 2 ? int.Parse(args[2]) : 4;
                var frames = args.Length > 3 ? int.Parse(args[3]) : 64;
                var outDir = args.Length > 4 ? args[4] : ".";
                var qp = args.Length > 5 ? int.Parse(args[5]) : 28;
                var arm = ResolveArm(armName, slices);
                if (qp != 28)
                    arm = arm with { Name = $"{arm.Name.Trim()}-q{qp}", Qp = qp };
                Dump(arm, frames, outDir);
                break;
            }

            default:
                throw new ArgumentException(mode);
        }
    }

    private static (H264BaselineEncoder Enc, byte[][] Frames, byte[] Annex) Setup(Arm arm)
    {
        var frames = arm.Divergent
            ? GenerateDivergentFrames(W, H)
            : H264ResolutionSliceSweepBenchmarks.GenerateFrames(W, H);
        var annex = new byte[W * H * 2 + 1_048_576];
        var enc = new H264BaselineEncoder(W, H, new H264BaselineEncoderOptions
        {
            QuantizationParameter = arm.Qp,
            KeyframeIntervalFrames = int.MaxValue,
            LevelIdc = 40,
            SliceCount = arm.Slices,
            UseMotionSatd = arm.Satd,
            MaxReferenceFrames = arm.MaxRef,
            SubPartitionRangeCap = arm.RangeCap,
            MotionSearchEffortCapPerMb = arm.EffortCap,
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
        var tiers = new long[arms.Length][];
        var mbs = new long[arms.Length];
        for (var a = 0; a < arms.Length; a++)
        {
            encs[a] = Setup(arms[a]);
            ms[a] = new double[Rounds];
            tiers[a] = new long[3];
            H264PInterDiagnostics.DisableRef1TieMargin = arms[a].DisableRef1Margin;
            H264PInterDiagnostics.DisableSlicePartitionBalance = arms[a].BalanceOff;
            H264PInterDiagnostics.DisableUnifiedQuadrantGate = arms[a].QgateOff;
            H264PInterDiagnostics.SubPartBudgetDivisorOverride = arms[a].SubPartDivisor;
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
                H264PInterDiagnostics.DisableSlicePartitionBalance = arms[a].BalanceOff;
                H264PInterDiagnostics.DisableUnifiedQuadrantGate = arms[a].QgateOff;
                H264PInterDiagnostics.SubPartBudgetDivisorOverride = arms[a].SubPartDivisor;
                H264PInterDiagnostics.ResetPhaseCounts();
                sw.Restart();
                for (var i = 0; i < ChunkFrames; i++)
                    Encode(encs[a].Enc, encs[a].Frames, encs[a].Annex, frameIdx[a]++, idr: false);
                sw.Stop();
                ms[a][round] = sw.Elapsed.TotalMilliseconds / ChunkFrames;
                var (hx, f1) = H264PInterDiagnostics.ReadMeSearchCounts();
                var (t1, t2, t3) = H264PInterDiagnostics.ReadMeBudgetTierCounts();
                hex[a] += hx;
                fb[a] += f1;
                tiers[a][0] += t1;
                tiers[a][1] += t2;
                tiers[a][2] += t3;
                mbs[a] += ChunkFrames;
            }
        }

        H264PInterDiagnostics.CollectPhaseCounts = false;
        H264PInterDiagnostics.DisableRef1TieMargin = false;
        H264PInterDiagnostics.DisableSlicePartitionBalance = false;
        H264PInterDiagnostics.DisableUnifiedQuadrantGate = false;
        H264PInterDiagnostics.SubPartBudgetDivisorOverride = null;
        for (var a = 0; a < arms.Length; a++)
        {
            Array.Sort(ms[a]);
            var med = ms[a][ms[a].Length / 2];
            Console.WriteLine(
                $"{arms[a].Name} {med,7:F2} ms/frame (min {ms[a][0]:F2} max {ms[a][^1]:F2})  " +
                $"hex/frame={(double)hex[a] / mbs[a],8:F1} exhaustive/frame={(double)fb[a] / mbs[a],6:F1}  " +
                $"tierMbs/frame={(double)tiers[a][0] / mbs[a]:F0}/{(double)tiers[a][1] / mbs[a]:F0}/{(double)tiers[a][2] / mbs[a]:F0}");
            encs[a].Enc.Dispose();
        }
    }

    /// <summary>
    /// Single-arm diagnostic run: <c>phases</c> prints the frame-phase wall breakdown plus managed
    /// allocation per frame, <c>mb</c> the per-macroblock Phase2 timing split, <c>satd</c> the SATD
    /// atom-DAG cache report. Timed frames run after a warm-up so the DPB and caches are hot.
    /// </summary>
    private static void Diagnose(string mode, Arm arm, int frames)
    {
        var (enc, srcFrames, annex) = Setup(arm);
        H264PInterDiagnostics.DisableUnifiedQuadrantGate = arm.QgateOff;
        var idx = 0;
        Encode(enc, srcFrames, annex, idx++, idr: true);
        for (var i = 0; i < 8; i++)
            Encode(enc, srcFrames, annex, idx++, idr: false);

        switch (mode)
        {
            case "phases":
                H264PInterDiagnostics.ResetFramePhases();
                H264PInterDiagnostics.CollectFramePhases = true;
                break;
            case "mb":
                H264PInterDiagnostics.ResetPhase2Timing();
                H264PInterDiagnostics.CollectPhase2Timing = true;
                break;
            case "satd":
                H264MotionSatdDagDiagnostics.Reset();
                H264MotionSatdDagDiagnostics.Enabled = true;
                break;
            default:
                throw new ArgumentException(mode);
        }

        var allocBefore = GC.GetTotalAllocatedBytes(precise: true);
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < frames; i++)
            Encode(enc, srcFrames, annex, idx++, idr: false);
        sw.Stop();
        var allocAfter = GC.GetTotalAllocatedBytes(precise: true);

        Console.WriteLine($"// {mode} arm={arm.Name} frames={frames} wall={sw.Elapsed.TotalMilliseconds / frames:F2} ms/frame");
        Console.WriteLine(
            $"// alloc/frame={(allocAfter - allocBefore) / (double)frames / 1024.0:F1} KiB  " +
            $"gc0={GC.CollectionCount(0) - gen0Before} gc1={GC.CollectionCount(1) - gen1Before} gc2={GC.CollectionCount(2) - gen2Before}");
        switch (mode)
        {
            case "phases":
                H264PInterDiagnostics.CollectFramePhases = false;
                Console.WriteLine(H264PInterDiagnostics.BuildFramePhaseReport(reset: true));
                if (Environment.GetEnvironmentVariable("KILN_PROBE_ROWSTATS") == "1")
                    Console.WriteLine(H264PInterDiagnostics.BuildRowStatsReport(reset: true));
                break;
            case "mb":
                H264PInterDiagnostics.CollectPhase2Timing = false;
                Console.WriteLine(H264PInterDiagnostics.BuildPhase2TimingReport(reset: true));
                break;
            case "satd":
                H264MotionSatdDagDiagnostics.Enabled = false;
                Console.WriteLine(H264MotionSatdDagDiagnostics.BuildReport(reset: true));
                break;
        }

        H264PInterDiagnostics.DisableUnifiedQuadrantGate = false;
        enc.Dispose();
    }

    /// <summary>
    /// Writes an Annex-B stream for one arm (IDR then P-frames over the synthetic cycle) plus, once,
    /// the raw source YUV, so PSNR/bitrate can be computed externally with ffmpeg.
    /// </summary>
    private static void Dump(Arm arm, int frames, string outDir)
    {
        var (enc, srcFrames, annex) = Setup(arm);
        H264PInterDiagnostics.DisableUnifiedQuadrantGate = arm.QgateOff;
        var srcPath = System.IO.Path.Combine(outDir, arm.Divergent ? "source-div.yuv" : "source.yuv");
        using var src = System.IO.File.Exists(srcPath) ? null : System.IO.File.Create(srcPath);
        using var bs = System.IO.File.Create(System.IO.Path.Combine(outDir, $"{arm.Name.Trim()}.h264"));
        long bytes = 0;
        for (var i = 0; i < frames; i++)
        {
            var n = Encode(enc, srcFrames, annex, i, idr: i == 0);
            bs.Write(annex, 0, n);
            bytes += n;
            src?.Write(srcFrames[i % H264ResolutionSliceSweepBenchmarks.FrameCycle]);
        }

        Console.WriteLine($"// dump arm={arm.Name} frames={frames} bytes={bytes} kbitPerFrame={bytes * 8.0 / frames / 1000.0:F1}");
        H264PInterDiagnostics.DisableUnifiedQuadrantGate = false;
        enc.Dispose();
    }

    /// <summary>
    /// Divergent-motion content for sub-partition search evaluation: the same value-noise texture
    /// recipe as <see cref="H264ResolutionSliceSweepBenchmarks.GenerateFrames"/>, but the top half
    /// scrolls left and the bottom half scrolls right at 6 px/frame, with two 96x96 squares moving
    /// vertically in opposite directions. Macroblocks on the half boundary and around the squares
    /// have genuinely divergent per-quadrant motion (12 px/frame split, 24 px against ref1).
    /// </summary>
    internal static byte[][] GenerateDivergentFrames(int w, int h)
    {
        const int Cycle = H264ResolutionSliceSweepBenchmarks.FrameCycle;
        const int Step = 6;
        var ys = w * h;
        var uv = ys / 4;
        var margin = Step * Cycle;
        var texW = w + 2 * margin;
        var tex = new byte[texW * h];
        var rng = new Random(20260825);
        var latW = texW / 16 + 2;
        var latH = h / 16 + 2;
        var lattice = new byte[latW * latH];
        rng.NextBytes(lattice);
        for (var y = 0; y < h; y++)
        {
            var ly = y / 16;
            var fy = (y & 15) / 16.0;
            for (var x = 0; x < texW; x++)
            {
                var lx = x / 16;
                var fx = (x & 15) / 16.0;
                var v00 = lattice[ly * latW + lx];
                var v10 = lattice[ly * latW + lx + 1];
                var v01 = lattice[(ly + 1) * latW + lx];
                var v11 = lattice[(ly + 1) * latW + lx + 1];
                var v = (v00 * (1 - fx) + v10 * fx) * (1 - fy) + (v01 * (1 - fx) + v11 * fx) * fy;
                tex[y * texW + x] = (byte)(48 + v * 160.0 / 255.0);
            }
        }

        var frames = new byte[Cycle][];
        for (var f = 0; f < Cycle; f++)
        {
            var frame = new byte[ys + 2 * uv];
            var yPlane = frame.AsSpan(0, ys);
            var uPlane = frame.AsSpan(ys, uv);
            var vPlane = frame.AsSpan(ys + uv, uv);
            var topShift = margin - f * Step;
            var botShift = f * Step;
            var half = h / 2;
            for (var row = 0; row < h; row++)
            {
                var shift = row < half ? topShift : botShift;
                tex.AsSpan(row * texW + shift, w).CopyTo(yPlane.Slice(row * w, w));
            }

            var noise = new Random(777_100 + f);
            for (var i = 0; i < ys; i++)
            {
                yPlane[i] = (byte)Math.Clamp(yPlane[i] + noise.Next(-2, 3), 0, 255);
            }

            var side = Math.Min(96, Math.Min(w, h) / 4);
            var byDown = (h / 4 + f * Step * 2) % Math.Max(1, h - side);
            var byUp = (3 * h / 4 - f * Step * 2 + h) % Math.Max(1, h - side);
            for (var yy = 0; yy < side; yy++)
            {
                yPlane.Slice((byDown + yy) * w + w / 4, side).Fill(235);
                yPlane.Slice((byUp + yy) * w + 3 * w / 4, side).Fill(20);
            }

            uPlane.Fill(118);
            vPlane.Fill(138);
            var cNoise = new Random(888_100 + f);
            for (var i = 0; i < uv; i++)
            {
                uPlane[i] = (byte)Math.Clamp(uPlane[i] + cNoise.Next(-1, 2), 0, 255);
                vPlane[i] = (byte)Math.Clamp(vPlane[i] + cNoise.Next(-1, 2), 0, 255);
            }

            frames[f] = frame;
        }

        return frames;
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
