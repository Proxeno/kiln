using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Kiln;
using Kiln.Internal.H264;

namespace Kiln.Benchmarks;

/// <summary>
/// Diagnostic: per-frame encoder-reconstruction vs ffmpeg-decode parity on
/// high-motion content, localising the first mismatching MBs with their coded refIdx/partition/skip
/// state. Not a benchmark; invoked via <c>--drift-probe &lt;outdir&gt;</c>.
/// </summary>
internal static class H264DriftProbe
{
    public static void Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        const int W = 640;
        const int H = 480;
        const int Frames = 6;
        const int Qp = 23;
        var mbW = W / 16;
        var mbH = H / 16;
        var mbCount = mbW * mbH;
        var frames = H264SliceSweepQuickHarness.HighMotionFrames(W, H);
        var ys = W * H;
        var uv = ys / 4;
        var annex = new byte[ys * 2 + 1_048_576];

        foreach (var slices in new[] { 1, 4 })
        {
            using var enc = new H264BaselineEncoder(W, H, new H264BaselineEncoderOptions
            {
                QuantizationParameter = Qp,
                KeyframeIntervalFrames = int.MaxValue,
                LevelIdc = 40,
                SliceCount = slices,
            });
            var shared = (H264FrameSharedState)typeof(H264BaselineEncoder)
                .GetField("_frameShared", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(enc)!;

            var reconPerFrame = new byte[Frames][];
            var refIdxPerFrame = new byte[Frames][];
            var partPerFrame = new H264MotionEstimator.McPartition[Frames][];
            var skipPerFrame = new bool[Frames][];
            var mvPerFrame = new H264MotionEstimator.Mv[Frames][];
            var subMvPerFrame = new H264MotionEstimator.Mv[Frames][];
            var streamPath = Path.Combine(outDir, $"probe_s{slices}.264");
            using (var fs = File.Create(streamPath))
            {
                for (var i = 0; i < Frames; i++)
                {
                    var f = frames[i % frames.Length];
                    var n = enc.EncodeFrame(
                        f.AsSpan(0, ys), f.AsSpan(ys, uv), f.AsSpan(ys + uv, uv), W, W / 2, annex, forceKeyframe: i == 0);
                    fs.Write(annex.AsSpan(0, n));
                    reconPerFrame[i] = shared.RecY.AsSpan(0, ys).ToArray();
                    refIdxPerFrame[i] = (byte[])shared.MbRefIdx.Clone();
                    partPerFrame[i] = (H264MotionEstimator.McPartition[])shared.MbPartitions.Clone();
                    skipPerFrame[i] = (bool[])shared.MbIsSkip.Clone();
                    mvPerFrame[i] = (H264MotionEstimator.Mv[])shared.MbMvs.Clone();
                    subMvPerFrame[i] = (H264MotionEstimator.Mv[])shared.MbSubPartMvs.Clone();
                }
            }

            var decoded = FfmpegDecode(streamPath, W, H, Frames);
            Console.WriteLine($"===== slices={slices} =====");
            for (var i = 0; i < Frames; i++)
            {
                var dec = decoded.AsSpan(i * (ys + 2 * uv), ys);
                var rec = reconPerFrame[i];
                var badPixels = 0;
                var badMbs = 0;
                var reported = 0;
                for (var mb = 0; mb < mbCount; mb++)
                {
                    var mbx = mb % mbW;
                    var mby = mb / mbW;
                    var mbBad = 0;
                    var maxDelta = 0;
                    for (var r = 0; r < 16; r++)
                    {
                        var off = (mby * 16 + r) * W + mbx * 16;
                        for (var c = 0; c < 16; c++)
                        {
                            var d = Math.Abs(dec[off + c] - rec[off + c]);
                            if (d != 0)
                            {
                                mbBad++;
                                if (d > maxDelta)
                                {
                                    maxDelta = d;
                                }
                            }
                        }
                    }

                    badPixels += mbBad;
                    if (mbBad > 0)
                    {
                        badMbs++;
                        if (reported < 8)
                        {
                            reported++;
                            Console.WriteLine(
                                $"  f{i} mb({mbx},{mby}) badPx={mbBad} maxD={maxDelta} refIdx={refIdxPerFrame[i][mb]} part={partPerFrame[i][mb]} skip={(skipPerFrame[i][mb] ? 1 : 0)}");
                        }
                    }
                }

                Console.WriteLine($"  frame {i}: badMbs={badMbs}/{mbCount} badPixels={badPixels}");
            }

            Console.WriteLine($"  --- frame 2 MB map rows 9-13 cols 0-8 (slices={slices}) ---");
            for (var mby = 9; mby <= 13; mby++)
            {
                for (var mbx = 0; mbx <= 8; mbx++)
                {
                    var mb = mby * mbW + mbx;
                    var mv = mvPerFrame[2][mb];
                    var sm = subMvPerFrame[2];
                    Console.WriteLine(
                        $"    mb({mbx},{mby}) part={partPerFrame[2][mb]} skip={(skipPerFrame[2][mb] ? 1 : 0)} refIdx={refIdxPerFrame[2][mb]} " +
                        $"mv=({mv.X},{mv.Y}) " +
                        $"subMv=[({sm[mb * 4].X},{sm[mb * 4].Y})({sm[mb * 4 + 1].X},{sm[mb * 4 + 1].Y})({sm[mb * 4 + 2].X},{sm[mb * 4 + 2].Y})({sm[mb * 4 + 3].X},{sm[mb * 4 + 3].Y})]");
                }
            }
        }
    }

    private static byte[] FfmpegDecode(string path, int w, int h, int frames)
    {
        var outPath = path + ".yuv";
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", path, "-f", "rawvideo", "-pix_fmt", "yuv420p", outPath })
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg failed: {err}");
        }

        var bytes = File.ReadAllBytes(outPath);
        if (bytes.Length < frames * w * h * 3 / 2)
        {
            throw new InvalidOperationException($"short decode: {bytes.Length}");
        }

        return bytes;
    }
}
