using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Kiln;
using Kiln.Internal.H264;

namespace Kiln.Benchmarks;

/// <summary>
/// The scalar-residue inventory's wall instrument: how much encoder wall do kiln's vectorized
/// paths actually own?
///
/// <para>Two independent ablation axes, crossed so the packet can attribute between them:</para>
/// <list type="bullet">
///   <item><description><c>hardwareKernels=false</c> — <see cref="H264BaselineEncoderOptions.PreferHardwareIntrinsics"/>
///   false replaces the whole <see cref="IH264KernelSet"/> tier with <see cref="ScalarKernelSet"/>.
///   Read in the encoder constructor, so it cannot silently fail to apply.</description></item>
///   <item><description><c>intrinsics=false</c> — <see cref="H264IntrinsicsPreference.Scope"/>(false) clears the
///   <c>AsyncLocal</c> that gates the <c>H264IntrinsicsPreference.Use*</c> families. These are consulted
///   at call sites that run on <see cref="Parallel.For"/> workers, so the ablation depends on
///   <c>ExecutionContext</c> flow. <c>--verify-flow</c> is the negative control for that, because an
///   ablation that silently declines to apply looks exactly like a path that costs nothing.</description></item>
/// </list>
///
/// <para>Arms run interleaved within a round and the order reverses on alternate rounds, so ambient
/// drift is spread across arms rather than landing on one. The <c>aa</c> arm is a second copy of the
/// shipped configuration: it measures the resolution floor of the box, and a delta smaller than the
/// A/A spread is not a result.</para>
///
/// <para>Usage: <c>dotnet Kiln.Benchmarks.dll --scalar-residue [w] [h] [frames] [rounds]</c>.
/// One process prints one independent set of per-arm medians; run it N times for a distribution.</para>
/// </summary>
internal static class ScalarResidueProbe
{
    private const int DefaultW = 1920;
    private const int DefaultH = 1080;
    private const int DefaultFrames = 40;
    private const int DefaultRounds = 5;

    private sealed record Arm(string Name, bool HardwareKernels, bool Intrinsics);

    public static void Run(string[] args)
    {
        if (Array.IndexOf(args, "--verify-flow") >= 0)
        {
            VerifyFlow();
            return;
        }

        // "single" drives ONE arm per process. The kernel-set families are selected by the
        // KILN_H264_SCALAR_ABLATE environment variable, which is per-process, so a family sweep is
        // inherently a sweep over processes — which is also the unit the packet counts in.
        if (args.Length > 0 && args[0] == "single")
        {
            Single(args[1..]);
            return;
        }

        var w = args.Length > 0 ? int.Parse(args[0]) : DefaultW;
        var h = args.Length > 1 ? int.Parse(args[1]) : DefaultH;
        var frames = args.Length > 2 ? int.Parse(args[2]) : DefaultFrames;
        var rounds = args.Length > 3 ? int.Parse(args[3]) : DefaultRounds;

        // aa is the shipped configuration run twice: the A/A negative control.
        Arm[] arms =
        [
            new("shipped", HardwareKernels: true, Intrinsics: true),
            new("aa", HardwareKernels: true, Intrinsics: true),
            new("noHardwareKernels", HardwareKernels: false, Intrinsics: true),
            new("noIntrinsics", HardwareKernels: true, Intrinsics: false),
            new("allScalar", HardwareKernels: false, Intrinsics: false),
        ];

        var encoders = new H264BaselineEncoder[arms.Length];
        var frameIdx = new int[arms.Length];
        var ms = new double[arms.Length][];
        var bytes = new long[arms.Length];

        var framesArr = H264ResolutionSliceSweepBenchmarks.GenerateFrames(w, h);
        var annex = new byte[w * h * 2 + 1_048_576];
        var ys = w * h;
        var uv = ys / 4;

        // The kernel-set tier is a construction-time property; build every encoder before any
        // timing so no arm pays another arm's JIT.
        for (var a = 0; a < arms.Length; a++)
        {
            encoders[a] = Build(w, h, arms[a]);
            ms[a] = new double[rounds];
            Prime(encoders[a], framesArr, annex, ref frameIdx[a], ys, uv, w);
        }

        Console.WriteLine(
            $"// scalar-residue {w}x{h} frames={frames} rounds={rounds} " +
            $"tier={H264KernelSet.CreateBest().GetType().Name} pid={Environment.ProcessId}");

        for (var round = 0; round < rounds; round++)
        {
            // Reverse the arm order on alternate rounds so a monotonic drift in the box cannot
            // favour whichever arm is always measured first.
            var order = new int[arms.Length];
            for (var i = 0; i < arms.Length; i++)
                order[i] = round % 2 == 0 ? i : arms.Length - 1 - i;

            foreach (var a in order)
            {
                using (new H264IntrinsicsPreference.Scope(arms[a].Intrinsics))
                {
                    var sw = Stopwatch.StartNew();
                    for (var i = 0; i < frames; i++)
                        bytes[a] += Encode(encoders[a], framesArr, annex, frameIdx[a]++, ys, uv, w);
                    sw.Stop();
                    ms[a][round] = sw.Elapsed.TotalMilliseconds / frames;
                }
            }
        }

        for (var a = 0; a < arms.Length; a++)
        {
            var sorted = (double[])ms[a].Clone();
            Array.Sort(sorted);
            var med = sorted[sorted.Length / 2];
            var cv = Cv(ms[a]);
            Console.WriteLine(
                $"ARM {arms[a].Name,-18} med={med,8:F3} min={sorted[0],8:F3} max={sorted[^1],8:F3} " +
                $"cv={cv,6:F2}% bytesPerFrame={bytes[a] / (long)(frames * rounds)}");
        }

        for (var a = 0; a < arms.Length; a++)
            encoders[a].Dispose();
    }

    /// <summary>
    /// One arm per process, selected entirely by environment (<c>KILN_H264_SCALAR_ABLATE</c>). The
    /// driver alternates the family order between rounds so a drifting box cannot favour whichever
    /// family is always measured first, and an <c>ablate=none</c> control prices the wrapper itself.
    /// </summary>
    private static void Single(string[] args)
    {
        var w = args.Length > 0 ? int.Parse(args[0]) : DefaultW;
        var h = args.Length > 1 ? int.Parse(args[1]) : DefaultH;
        var frames = args.Length > 2 ? int.Parse(args[2]) : DefaultFrames;
        var rounds = args.Length > 3 ? int.Parse(args[3]) : DefaultRounds;

        var ablate = Environment.GetEnvironmentVariable("KILN_H264_SCALAR_ABLATE") ?? "(unset)";
        var framesArr = H264ResolutionSliceSweepBenchmarks.GenerateFrames(w, h);
        var annex = new byte[w * h * 2 + 1_048_576];
        var ys = w * h;
        var uv = ys / 4;

        var enc = Build(w, h, new Arm("single", HardwareKernels: true, Intrinsics: true));
        var idx = 0;
        Prime(enc, framesArr, annex, ref idx, ys, uv, w);

        var ms = new double[rounds];
        var bytes = 0L;
        for (var round = 0; round < rounds; round++)
        {
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < frames; i++)
                bytes += Encode(enc, framesArr, annex, idx++, ys, uv, w);
            sw.Stop();
            ms[round] = sw.Elapsed.TotalMilliseconds / frames;
        }

        var sorted = (double[])ms.Clone();
        Array.Sort(sorted);
        Console.WriteLine(
            $"SINGLE ablate={ablate,-14} tier={H264KernelSet.CreateBest().GetType().Name} " +
            $"med={sorted[sorted.Length / 2],8:F3} min={sorted[0],8:F3} max={sorted[^1],8:F3} " +
            $"cv={Cv(ms),6:F2}% bytesPerFrame={bytes / (long)(frames * rounds)}");
        enc.Dispose();
    }

    private static H264BaselineEncoder Build(int w, int h, Arm arm) =>
        new(w, h, new H264BaselineEncoderOptions
        {
            QuantizationParameter = 28,
            KeyframeIntervalFrames = int.MaxValue,
            LevelIdc = 40,
            SliceCount = 4,
            UseMotionSatd = true,
            MaxReferenceFrames = 2,
            SubPartitionRangeCap = 16,
            PreferHardwareIntrinsics = arm.HardwareKernels,
        });

    /// <summary>
    /// How many frames run before any measurement. This is not politeness: RyuJIT tiers the encode
    /// body up over the first few dozen calls, and a timing that starts while the code is still at
    /// tier 0 measures the JIT. The first probe of this packet primed with four frames and produced
    /// 11.6–50 ms spreads on identical work; the spread was tier-up, not the box.
    /// </summary>
    private const int WarmupFrames = 30;

    private static void Prime(
        H264BaselineEncoder enc, byte[][] frames, byte[] annex, ref int idx, int ys, int uv, int w)
    {
        _ = Encode(enc, frames, annex, idx++, ys, uv, w, idr: true);
        for (var i = 0; i < WarmupFrames; i++)
            _ = Encode(enc, frames, annex, idx++, ys, uv, w);
    }

    private static int Encode(
        H264BaselineEncoder enc, byte[][] frames, byte[] annex, int index, int ys, int uv, int w, bool idr = false)
    {
        var f = frames[index % H264ResolutionSliceSweepBenchmarks.FrameCycle];
        return enc.EncodeFrame(f.AsSpan(0, ys), f.AsSpan(ys, uv), f.AsSpan(ys + uv, uv), w, w / 2, annex, idr);
    }

    private static double Cv(double[] v)
    {
        double sum = 0;
        foreach (var x in v)
            sum += x;
        var mean = sum / v.Length;
        if (mean <= 0)
            return 0;
        double acc = 0;
        foreach (var x in v)
            acc += (x - mean) * (x - mean);
        return Math.Sqrt(acc / v.Length) / mean * 100.0;
    }

    /// <summary>
    /// Negative control for the <see cref="H264IntrinsicsPreference"/> ablation. That flag is an
    /// <c>AsyncLocal</c> read at call sites which execute inside <see cref="Parallel.For"/>, so the
    /// ablation only applies if <c>ExecutionContext</c> flows into the workers. If this prints
    /// <c>flow=OK</c> the ablation is real; if it prints <c>flow=BROKEN</c> then an unchanged
    /// <c>noIntrinsics</c> arm means "the scope never arrived", not "the paths are free".
    /// </summary>
    private static void VerifyFlow()
    {
        var observed = new bool[Environment.ProcessorCount];
        var inScope = H264IntrinsicsPreference.PreferIntrinsics;
        using (new H264IntrinsicsPreference.Scope(false))
        {
            Parallel.For(0, observed.Length, i => observed[i] = H264IntrinsicsPreference.PreferIntrinsics);
        }

        var sawFalse = 0;
        foreach (var v in observed)
        {
            if (!v)
                sawFalse++;
        }

        var afterScope = H264IntrinsicsPreference.PreferIntrinsics;
        var verdict = sawFalse == observed.Length ? "OK" : "BROKEN";
        Console.WriteLine(
            $"// verify-flow default={inScope} workersObservedFalse={sawFalse}/{observed.Length} " +
            $"restoredAfterScope={afterScope} flow={verdict}");
    }
}
