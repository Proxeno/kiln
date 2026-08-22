using BenchmarkDotNet.Attributes;
using Kiln.Internal.H264;

namespace Kiln.Benchmarks;

/// <summary>
/// Motion-estimation hot paths: multi-size SAD and full <see cref="H264MotionEstimator.SearchMb16x16"/>.
/// Run: <c>dotnet run -c Release --project benchmarks/Kiln.Benchmarks --filter '*MotionEstimator*'</c>
/// </summary>
[MemoryDiagnoser]
[MinColumn, MaxColumn, MeanColumn, StdErrorColumn]
public class H264MotionEstimatorBenchmarks
{
    private const int RefStride = 256;
    private const int RefH = 128;

    private byte[] _current16 = null!;
    private byte[] _current8 = null!;
    private byte[] _reference = null!;
    private byte[] _referenceStride720 = null!;
    private IH264KernelSet _kernels = null!;
    private H264MotionEstimator.Mv _pred;

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(17);
        _current16 = new byte[256];
        _current8 = new byte[64];
        rng.NextBytes(_current16);
        rng.NextBytes(_current8);

        _reference = new byte[RefStride * RefH];
        rng.NextBytes(_reference);

        const int stride720 = 720;
        _referenceStride720 = new byte[stride720 * 96];
        rng.NextBytes(_referenceStride720);

        _kernels = H264KernelSet.CreateBest();
        _pred = new H264MotionEstimator.Mv(0, 0);
    }

    [Benchmark(Baseline = true)]
    public int Sad16x16_Scalar() =>
        H264MotionSad.Sad16x16Scalar(_current16, 16, _reference.AsSpan(48 * RefStride + 48), RefStride);

    [Benchmark]
    public int Sad16x16_Dispatch() =>
        H264MotionSad.Sad16x16(_current16, 16, _reference.AsSpan(48 * RefStride + 48), RefStride);

    [Benchmark]
    public int Sad16x16_Avx2()
    {
        if (!H264MotionSad.IsAvx2MotionSadSupported)
        {
            return 0;
        }

        return H264MotionSad.Sad16x16Avx2(_current16, 16, _reference.AsSpan(48 * RefStride + 48), RefStride);
    }

    [Benchmark]
    public int Sad16x16_Ssse3()
    {
        if (!H264MotionSad.IsSupported)
        {
            return 0;
        }

        return H264MotionSad.Sad16x16Ssse3(_current16, 16, _reference.AsSpan(48 * RefStride + 48), RefStride);
    }

    [Benchmark]
    public int Sad8x8_Scalar() =>
        H264MotionSad.Sad8x8Scalar(_current8, 8, _reference.AsSpan(56 * RefStride + 56), RefStride);

    [Benchmark]
    public int Sad8x8_Dispatch() =>
        H264MotionSad.Sad8x8(_current8, 8, _reference.AsSpan(56 * RefStride + 56), RefStride);

    [Benchmark]
    public int Sad8x16_Avx2()
    {
        if (!H264MotionSad.IsAvx2MotionSadSupported)
        {
            return 0;
        }

        return H264MotionSad.Sad8x16Avx2(_current16, 16, _reference.AsSpan(56 * RefStride + 56), RefStride);
    }

    [Benchmark]
    public int Sad8x8_Avx2()
    {
        if (!H264MotionSad.IsAvx2MotionSadSupported)
        {
            return 0;
        }

        return H264MotionSad.Sad8x8Avx2(_current8, 8, _reference.AsSpan(56 * RefStride + 56), RefStride);
    }

    [Benchmark]
    public int Sad16x16_Dispatch_Stride720() =>
        H264MotionSad.Sad16x16(
            _current16,
            16,
            _referenceStride720.AsSpan(32 * 720 + 100),
            720);

    [Benchmark]
    public int SearchMb16x16_SearchRange8() =>
        H264MotionEstimator.SearchMb16x16(
                _current16,
                16,
                _reference,
                RefStride,
                mbX: 48,
                mbY: 48,
                _pred,
                searchRange: 8,
                useMotionSatd: true,
                kernels: _kernels,
                pictureWidth: 0,
                pictureHeight: 0,
                fractionalPelRefinementRounds: 2)
            .BestSad;
}
