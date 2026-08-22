using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using Kiln;

namespace Kiln.Benchmarks;

/// <summary>
/// Single-frame H.264 baseline encode (16×16): IDR via <c>forceKeyframe</c>, P after priming IDR.
/// Compare <see cref="H264BaselineEncoderOptions.PreferHardwareIntrinsics"/> true vs false on SIMD-capable hosts.
/// </summary>
[MemoryDiagnoser]
[MinColumn, MaxColumn, MeanColumn, StdErrorColumn]
public class H264SingleFrameBenchmarks
{
    private const int W = 16;
    private const int H = 16;

    private byte[] _i420Busy = null!;
    private byte[] _i420Flat = null!;
    private byte[] _annex = null!;

    private H264BaselineEncoder _simdIdrBusy = null!;
    private H264BaselineEncoder _scalarIdrBusy = null!;
    private H264BaselineEncoder _simdIdrFlat = null!;
    private H264BaselineEncoder _scalarIdrFlat = null!;
    private H264BaselineEncoder _simdP = null!;
    private H264BaselineEncoder _scalarP = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var ySize = W * H;
        var uvSize = ySize / 4;
        var total = ySize + 2 * uvSize;

        _i420Busy = new byte[total];
        new Random(42).NextBytes(_i420Busy);

        _i420Flat = new byte[total];
        _i420Flat.AsSpan().Fill(128);

        _annex = new byte[256 * 1024];

        static H264BaselineEncoderOptions Opts(bool intrinsics) =>
            new()
            {
                QuantizationParameter = 28,
                KeyframeIntervalFrames = 10_000,
                PreferHardwareIntrinsics = intrinsics,
            };

        _simdIdrBusy = new H264BaselineEncoder(W, H, Opts(true));
        _scalarIdrBusy = new H264BaselineEncoder(W, H, Opts(false));
        _simdIdrFlat = new H264BaselineEncoder(W, H, Opts(true));
        _scalarIdrFlat = new H264BaselineEncoder(W, H, Opts(false));

        _simdP = new H264BaselineEncoder(W, H, Opts(true));
        _scalarP = new H264BaselineEncoder(W, H, Opts(false));
        EncodeIdr(_simdP, _i420Busy, _annex);
        EncodeIdr(_scalarP, _i420Busy, _annex);
    }

    private static int EncodeIdr(H264BaselineEncoder enc, byte[] i420, Span<byte> annex)
    {
        var ySize = W * H;
        var uvSize = ySize / 4;
        var y = i420.AsSpan(0, ySize);
        var u = i420.AsSpan(ySize, uvSize);
        var v = i420.AsSpan(ySize + uvSize, uvSize);
        return enc.EncodeFrame(y, u, v, W, W / 2, annex, forceKeyframe: true);
    }

    private int EncodeP(H264BaselineEncoder enc, byte[] i420) =>
        EncodeP(enc, i420, _annex);

    private static int EncodeP(H264BaselineEncoder enc, byte[] i420, Span<byte> annex)
    {
        var ySize = W * H;
        var uvSize = ySize / 4;
        var y = i420.AsSpan(0, ySize);
        var u = i420.AsSpan(ySize, uvSize);
        var v = i420.AsSpan(ySize + uvSize, uvSize);
        return enc.EncodeFrame(y, u, v, W, W / 2, annex, forceKeyframe: false);
    }

    [Benchmark(Baseline = true)]
    public int Idr_Busy_Simd() =>
        EncodeIdr(_simdIdrBusy, _i420Busy, _annex);

    [Benchmark]
    public int Idr_Busy_Scalar() =>
        EncodeIdr(_scalarIdrBusy, _i420Busy, _annex);

    [Benchmark]
    public int Idr_Flat_Simd() =>
        EncodeIdr(_simdIdrFlat, _i420Flat, _annex);

    [Benchmark]
    public int Idr_Flat_Scalar() =>
        EncodeIdr(_scalarIdrFlat, _i420Flat, _annex);

    [Benchmark]
    public int P_Busy_Simd() =>
        EncodeP(_simdP, _i420Busy);

    [Benchmark]
    public int P_Busy_Scalar() =>
        EncodeP(_scalarP, _i420Busy);

    /// <summary>
    /// Repeated 16×16 P-frame encode in a tight loop: amortizes one-time setup over 32 calls so the
    /// per-call SIMD vs scalar gap isolates the kernel/dispatcher path from constructor / first-call
    /// overhead. Pairs with <see cref="P_Busy_Simd"/> / <see cref="P_Busy_Scalar"/> to answer the
    /// senior parent's diagnostic: "is SIMD slower for a single tiny image, or slower for the same
    /// kernel shape inside a normal frame?".
    /// </summary>
    [Benchmark]
    public int P_Busy_Simd_x32()
    {
        var total = 0;
        for (var i = 0; i < 32; i++)
        {
            total += EncodeP(_simdP, _i420Busy);
        }

        return total;
    }

    [Benchmark]
    public int P_Busy_Scalar_x32()
    {
        var total = 0;
        for (var i = 0; i < 32; i++)
        {
            total += EncodeP(_scalarP, _i420Busy);
        }

        return total;
    }

    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--quick")
        {
            QuickStopwatchBench();
            return;
        }

        foreach (var summary in BenchmarkSwitcher
                     .FromAssembly(typeof(H264SingleFrameBenchmarks).Assembly)
                     .Run(args, KilnBenchmarkHostConfig.Create()))
        {
            GC.KeepAlive(summary);
        }
    }

    /// <summary>
    /// Stopwatch microbench at realistic resolutions (live WebRTC pipeline). Prints per-frame ms
    /// for SIMD-on and SIMD-off in the same run so the per-resolution scalar vs SIMD delta is
    /// directly comparable against the 16×16 BDN ratio. Pairs with <see cref="H264SingleFrameBenchmarks"/>.
    /// </summary>
    private static void QuickStopwatchBench()
    {
        Run(256, 224, intrinsics: true);
        Run(256, 224, intrinsics: false);
        Run(320, 240, intrinsics: true);
        Run(320, 240, intrinsics: false);
        Run(640, 480, intrinsics: true);
        Run(640, 480, intrinsics: false);
    }

    private static void Run(int w, int h, bool intrinsics = true)
    {
        var label = intrinsics ? "Simd  " : "Scalar";
        var ySize = w * h;
        var uvSize = ySize / 4;
        var i420 = new byte[ySize + 2 * uvSize];
        new Random(123).NextBytes(i420);
        var annex = new byte[w * h * 2 + 512_000];

        var enc = new H264BaselineEncoder(w, h, new H264BaselineEncoderOptions
        {
            QuantizationParameter = 28,
            KeyframeIntervalFrames = 60,
            PreferHardwareIntrinsics = intrinsics,
        });

        var y = i420.AsSpan(0, ySize);
        var u = i420.AsSpan(ySize, uvSize);
        var v = i420.AsSpan(ySize + uvSize, uvSize);

        for (var i = 0; i < 5; i++)
        {
            _ = enc.EncodeFrame(y, u, v, w, w / 2, annex, forceKeyframe: i == 0);
        }

        const int iters = 30;
        var sw = Stopwatch.StartNew();
        var bytes = 0L;
        for (var i = 0; i < iters; i++)
        {
            bytes += enc.EncodeFrame(y, u, v, w, w / 2, annex, forceKeyframe: false);
        }

        sw.Stop();
        var msPerFrame = sw.Elapsed.TotalMilliseconds / iters;
        Console.WriteLine($"{w}x{h} P-frames   [{label}]: {msPerFrame:F2} ms/frame ({1000.0 / msPerFrame:F1} fps cap)  avgBytes={bytes / iters}");

        var encIdr = new H264BaselineEncoder(w, h, new H264BaselineEncoderOptions
        {
            QuantizationParameter = 28,
            KeyframeIntervalFrames = 1,
            PreferHardwareIntrinsics = intrinsics,
        });
        for (var i = 0; i < 3; i++)
        {
            _ = encIdr.EncodeFrame(y, u, v, w, w / 2, annex, forceKeyframe: true);
        }

        sw.Restart();
        for (var i = 0; i < iters; i++)
        {
            _ = encIdr.EncodeFrame(y, u, v, w, w / 2, annex, forceKeyframe: true);
        }

        sw.Stop();
        msPerFrame = sw.Elapsed.TotalMilliseconds / iters;
        Console.WriteLine($"{w}x{h} IDR-frames [{label}]: {msPerFrame:F2} ms/frame ({1000.0 / msPerFrame:F1} fps cap)");
    }
}
