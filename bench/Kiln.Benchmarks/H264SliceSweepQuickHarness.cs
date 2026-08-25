using System;
using System.Diagnostics;
using System.IO;
using Kiln;
using Kiln.Internal.H264;

namespace Kiln.Benchmarks;

/// <summary>
/// Bounded stopwatch/quality harness for the issue #3 temporal-seed probe. Not a BenchmarkDotNet
/// run: each mode finishes in well under a minute per configuration and prints as it goes, so
/// interrupted runs still leave usable data.
/// <list type="bullet">
/// <item><c>--slice-quick</c>: resolution × slice-count P-frame ms/frame with the probe enabled and
/// disabled interleaved chunk-wise in the same process (guards against E-core scheduling drift),
/// plus per-frame hex-search / exhaustive-fallback / phase counters for each arm.</item>
/// <item><c>--slice-quality &lt;outdir&gt;</c>: encodes static / moving / high-motion / scene-cut
/// content at several QPs and slice counts for both arms and writes raw source + Annex B streams to
/// <paramref name="outdir"/> for external ffmpeg decode + PSNR comparison.</item>
/// </list>
/// </summary>
internal static class H264SliceSweepQuickHarness
{
    private static readonly (int W, int H)[] Resolutions = [(640, 480), (1280, 720), (1920, 1080)];
    private static readonly int[] SliceCounts = [1, 2, 4, 8];

    public static void RunPerf()
    {
        Console.WriteLine($"// slice-quick: procCount={Environment.ProcessorCount} kernels={H264KernelSet.CreateBest().GetType().Name}");
        Console.WriteLine("resolution slices arm    ms/frame(median of chunk means)  hex/frame fallback/frame phase2/frame skip/frame");
        foreach (var (w, h) in Resolutions)
        {
            var frames = H264ResolutionSliceSweepBenchmarks.GenerateFrames(w, h);
            foreach (var slices in SliceCounts)
            {
                MeasureConfig(w, h, slices, frames);
            }
        }
    }

    private static void MeasureConfig(int w, int h, int slices, byte[][] frames)
    {
        const int ChunkFrames = 12;
        const int Rounds = 5;
        var ys = w * h;
        var uv = ys / 4;
        var annex = new byte[ys * 2 + 1_048_576];

        H264BaselineEncoderOptions Opts() => new()
        {
            QuantizationParameter = 28,
            KeyframeIntervalFrames = int.MaxValue,
            LevelIdc = 40,
            SliceCount = slices,
        };

        using var encOn = new H264BaselineEncoder(w, h, Opts());
        using var encOff = new H264BaselineEncoder(w, h, Opts());

        var frameOn = 0;
        var frameOff = 0;

        int Encode(H264BaselineEncoder enc, int index, bool forceKeyframe)
        {
            var f = frames[index % H264ResolutionSliceSweepBenchmarks.FrameCycle];
            return enc.EncodeFrame(
                f.AsSpan(0, ys), f.AsSpan(ys, uv), f.AsSpan(ys + uv, uv), w, w / 2, annex, forceKeyframe);
        }

        // Prime both arms with their own arm setting active so their reference chains stay
        // internally consistent (the two arms produce different bitstreams by design).
        SetArm(disabled: false);
        Encode(encOn, frameOn++, forceKeyframe: true);
        for (var i = 0; i < 3; i++)
        {
            Encode(encOn, frameOn++, forceKeyframe: false);
        }

        SetArm(disabled: true);
        Encode(encOff, frameOff++, forceKeyframe: true);
        for (var i = 0; i < 3; i++)
        {
            Encode(encOff, frameOff++, forceKeyframe: false);
        }

        var msOn = new double[Rounds];
        var msOff = new double[Rounds];
        H264PInterDiagnostics.CollectPhaseCounts = true;
        H264PInterDiagnostics.ResetPhaseCounts();
        long hexOn = 0, fbOn = 0, p2On = 0, skOn = 0, framesOnCounted = 0;
        long hexOff = 0, fbOff = 0, p2Off = 0, skOff = 0, framesOffCounted = 0;
        long wgOn = 0, paOn = 0, ppOn = 0;
        long wgOff = 0, paOff = 0, ppOff = 0;
        var sw = new Stopwatch();
        for (var round = 0; round < Rounds; round++)
        {
            // Interleave arms within each round: On, Off, so slow ambient drift hits both equally.
            SetArm(disabled: false);
            H264PInterDiagnostics.ResetPhaseCounts();
            sw.Restart();
            for (var i = 0; i < ChunkFrames; i++)
            {
                Encode(encOn, frameOn++, forceKeyframe: false);
            }

            sw.Stop();
            msOn[round] = sw.Elapsed.TotalMilliseconds / ChunkFrames;
            var (sk1, p21, _) = H264PInterDiagnostics.ReadPhaseCounts();
            var (hx1, fb1) = H264PInterDiagnostics.ReadMeSearchCounts();
            var (wg1, pa1, pp1) = H264PInterDiagnostics.ReadTemporalProbeCounts();
            wgOn += wg1;
            paOn += pa1;
            ppOn += pp1;
            hexOn += hx1;
            fbOn += fb1;
            p2On += p21;
            skOn += sk1;
            framesOnCounted += ChunkFrames;

            SetArm(disabled: true);
            H264PInterDiagnostics.ResetPhaseCounts();
            sw.Restart();
            for (var i = 0; i < ChunkFrames; i++)
            {
                Encode(encOff, frameOff++, forceKeyframe: false);
            }

            sw.Stop();
            msOff[round] = sw.Elapsed.TotalMilliseconds / ChunkFrames;
            var (sk0, p20, _) = H264PInterDiagnostics.ReadPhaseCounts();
            var (hx0, fb0) = H264PInterDiagnostics.ReadMeSearchCounts();
            var (wg0, pa0, pp0) = H264PInterDiagnostics.ReadTemporalProbeCounts();
            wgOff += wg0;
            paOff += pa0;
            ppOff += pp0;
            hexOff += hx0;
            fbOff += fb0;
            p2Off += p20;
            skOff += sk0;
            framesOffCounted += ChunkFrames;
        }

        H264PInterDiagnostics.CollectPhaseCounts = false;
        SetArm(disabled: false);
        Report("on ", msOn, hexOn, fbOn, p2On, skOn, wgOn, paOn, ppOn, framesOnCounted);
        Report("off", msOff, hexOff, fbOff, p2Off, skOff, wgOff, paOff, ppOff, framesOffCounted);

        void Report(string arm, double[] ms, long hex, long fb, long p2, long sk, long wg, long pa, long pp, long n)
        {
            Array.Sort(ms);
            var median = ms[ms.Length / 2];
            Console.WriteLine(
                $"{w}x{h} s={slices} {arm} {median,8:F2} ms/frame  (min {ms[0]:F2} max {ms[^1]:F2})  " +
                $"hex={(double)hex / n,8:F1} fallback={(double)fb / n,7:F1} phase2={(double)p2 / n,8:F1} skip={(double)sk / n,8:F1} " +
                $"wideGate={(double)wg / n,7:F1} probes={(double)pa / n,7:F1} passes={(double)pp / n,7:F1}");
        }
    }

    /// <summary>Per-MB trace of rows straddling the second slice's top row (640x480, s=4 vs s=1).</summary>
    public static void RunTrace()
    {
        const int W = 640;
        const int H = 480;
        var frames = H264ResolutionSliceSweepBenchmarks.GenerateFrames(W, H);
        var ys = W * H;
        var uv = ys / 4;
        var annex = new byte[ys * 2 + 1_048_576];
        foreach (var slices in new[] { 1, 4 })
        {
            foreach (var mby in new[] { 7, 8, 9, 10 })
            {
                for (var mbx = 0; mbx < W / 16; mbx++)
                {
                    H264PInterDiagnostics.AddRuntimeTraceMbTarget(-1, mbx, mby);
                }
            }

            using var enc = new H264BaselineEncoder(W, H, new H264BaselineEncoderOptions
            {
                QuantizationParameter = 28,
                KeyframeIntervalFrames = int.MaxValue,
                LevelIdc = 40,
                SliceCount = slices,
            });
            for (var i = 0; i < 7; i++)
            {
                var f = frames[i % H264ResolutionSliceSweepBenchmarks.FrameCycle];
                enc.EncodeFrame(f.AsSpan(0, ys), f.AsSpan(ys, uv), f.AsSpan(ys + uv, uv), W, W / 2, annex, forceKeyframe: i == 0);
                if (i < 6)
                {
                    _ = H264PInterDiagnostics.BuildMbTraceReportAndReset();
                }
            }

            Console.WriteLine($"===== slices={slices} frame 6 rows 7-10 =====");
            Console.WriteLine(H264PInterDiagnostics.BuildMbTraceReportAndReset());
            H264PInterDiagnostics.ClearRuntimeTraceMbTargets();
        }
    }

    private static void SetArm(bool disabled)
    {
        H264PInterDiagnostics.DisableTemporalSeedProbe = disabled;
        H264PInterDiagnostics.DisableRef1TieMargin = disabled;
    }

    public static void RunQuality(string outDir)
    {
        Directory.CreateDirectory(outDir);
        int[] qps = [23, 28, 35];
        int[] sliceCounts = [1, 4];
        const int W = 640;
        const int H = 480;
        const int FrameCount = 24;
        Console.WriteLine("content        qp slices arm bytes");
        foreach (var (name, frames) in new (string, byte[][])[]
                 {
                     ("static", GenerateStatic(W, H)),
                     ("moving", H264ResolutionSliceSweepBenchmarks.GenerateFrames(W, H)),
                     ("highmotion", GenerateHighMotion(W, H)),
                     ("scenecut", GenerateSceneCut(W, H)),
                 })
        {
            WriteSource(outDir, name, W, H, frames, FrameCount);
            foreach (var qp in qps)
            {
                foreach (var slices in sliceCounts)
                {
                    foreach (var probeOn in new[] { true, false })
                    {
                        SetArm(disabled: !probeOn);
                        var bytes = EncodeToFile(outDir, name, W, H, frames, FrameCount, qp, slices, probeOn);
                        Console.WriteLine($"{name,-14} {qp,2} {slices,6} {(probeOn ? "on " : "off")} {bytes}");
                    }
                }
            }
        }

        SetArm(disabled: false);
    }

    private static void WriteSource(string outDir, string name, int w, int h, byte[][] frames, int frameCount)
    {
        using var fs = File.Create(Path.Combine(outDir, $"{name}_{w}x{h}.yuv"));
        for (var i = 0; i < frameCount; i++)
        {
            fs.Write(frames[i % frames.Length]);
        }
    }

    private static long EncodeToFile(
        string outDir, string name, int w, int h, byte[][] frames, int frameCount, int qp, int slices, bool probeOn)
    {
        var ys = w * h;
        var uv = ys / 4;
        var annex = new byte[ys * 2 + 1_048_576];
        using var enc = new H264BaselineEncoder(w, h, new H264BaselineEncoderOptions
        {
            QuantizationParameter = qp,
            KeyframeIntervalFrames = int.MaxValue,
            LevelIdc = 40,
            SliceCount = slices,
        });
        using var fs = File.Create(Path.Combine(outDir, $"{name}_qp{qp}_s{slices}_{(probeOn ? "on" : "off")}.264"));
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

    /// <summary>One generator frame repeated exactly — the skip-dominated best case.</summary>
    private static byte[][] GenerateStatic(int w, int h) =>
        [H264ResolutionSliceSweepBenchmarks.GenerateFrames(w, h)[0]];

    /// <summary>
    /// Fast diagonal global scroll (12 px/frame) of a textured lattice plus a 26 px/frame square —
    /// motion large enough that a tight ME range without a good seed would visibly miss.
    /// </summary>
    private static byte[][] GenerateHighMotion(int w, int h)
    {
        const int Cycle = 8;
        var ys = w * h;
        var uv = ys / 4;
        var pad = 12 * Cycle;
        var texW = w + pad;
        var texH = h + pad;
        var tex = new byte[texW * texH];
        var rng = new Random(4242);
        var latW = texW / 12 + 2;
        var latH = texH / 12 + 2;
        var lattice = new byte[latW * latH];
        rng.NextBytes(lattice);
        for (var y = 0; y < texH; y++)
        {
            for (var x = 0; x < texW; x++)
            {
                var v = lattice[(y / 12) * latW + x / 12];
                tex[y * texW + x] = (byte)(40 + (v * 170 / 255) + (((x / 6) + (y / 6)) & 1) * 12);
            }
        }

        var frames = new byte[Cycle][];
        for (var f = 0; f < Cycle; f++)
        {
            var frame = new byte[ys + 2 * uv];
            var yPlane = frame.AsSpan(0, ys);
            var shift = f * 12;
            for (var row = 0; row < h; row++)
            {
                tex.AsSpan((row + shift) * texW + shift, w).CopyTo(yPlane.Slice(row * w, w));
            }

            var side = 80;
            var bx = (f * 26) % Math.Max(1, w - side);
            var by = h / 3;
            for (var yy = 0; yy < side; yy++)
            {
                yPlane.Slice((by + yy) * w + bx, side).Fill(240);
            }

            frame.AsSpan(ys, uv).Fill(110);
            frame.AsSpan(ys + uv, uv).Fill(146);
            frames[f] = frame;
        }

        return frames;
    }

    /// <summary>Two unrelated textures alternated every 6 frames — the "genuinely lost" case the wide search exists for.</summary>
    private static byte[][] GenerateSceneCut(int w, int h)
    {
        var a = H264ResolutionSliceSweepBenchmarks.GenerateFrames(w, h);
        var b = GenerateHighMotion(w, h);
        var frames = new byte[12][];
        for (var i = 0; i < frames.Length; i++)
        {
            frames[i] = i / 6 % 2 == 0 ? a[i % a.Length] : b[i % b.Length];
        }

        return frames;
    }
}
