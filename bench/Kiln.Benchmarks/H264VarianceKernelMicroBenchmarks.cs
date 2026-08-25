using BenchmarkDotNet.Attributes;
using Kiln.Internal.H264;

namespace Kiln.Benchmarks;

/// <summary>
/// Isolates the 16×16 MB variance kernel (<see cref="IH264KernelSet.VarianceMb16x16"/>): with
/// adaptive quantisation enabled it runs over every MB of every frame (8 160 MBs at 1080p), plus
/// the Phase-2 inter path's per-MB search-range selection. The benchmark walks a full 1080p MB row
/// grid per invocation so the measured delta reflects the real per-frame call pattern (strided
/// plane reads, one call per MB) rather than a single hot-cache block.
/// </summary>
[MemoryDiagnoser]
[MinColumn, MaxColumn, MeanColumn, StdErrorColumn]
public class H264VarianceKernelMicroBenchmarks
{
    private const int W = 1920;
    private const int H = 1080;
    private const int MbW = W / 16;
    private const int MbH = H / 16;

    [ParamsAllValues]
    public bool PreferHardwareIntrinsics { get; set; }

    private byte[] _plane = null!;
    private IH264KernelSet _kernels = null!;

    [GlobalSetup]
    public void Setup()
    {
        _kernels = PreferHardwareIntrinsics ? H264KernelSet.CreateBest() : new ScalarKernelSet();
        _plane = new byte[W * H];
        new Random(42).NextBytes(_plane);
    }

    /// <summary>Full-frame AQ sweep: one variance per MB over the whole 1080p plane.</summary>
    [Benchmark]
    public long VarianceMb16x16_FullFrame()
    {
        long acc = 0;
        var plane = _plane.AsSpan();
        for (var mby = 0; mby < MbH; mby++)
        {
            for (var mbx = 0; mbx < MbW; mbx++)
            {
                acc += _kernels.VarianceMb16x16(plane.Slice(mby * 16 * W + mbx * 16), W);
            }
        }

        return acc;
    }
}
