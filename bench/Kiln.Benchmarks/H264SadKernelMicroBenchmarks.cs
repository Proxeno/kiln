using BenchmarkDotNet.Attributes;
using Kiln.Internal.H264;

namespace Kiln.Benchmarks;

/// <summary>
/// Isolates the H.264 4×4 hot-path primitives the encoder calls inside its candidate-mode loops, so
/// the per-kernel SIMD vs scalar delta can be measured without slice/macroblock dispatch overhead.
/// Pairs with <see cref="H264SingleFrameBenchmarks"/> (full-encode BDN at frame dimensions): if a
/// kernel is faster here but the full-encode bench shows SIMD slower, the regression lives in the
/// dispatcher path rather than the SIMD body itself.
///
/// The <see cref="PreferHardwareIntrinsics"/> bool is toggled via <see cref="ParamsAllValuesAttribute"/>
/// and selects <see cref="H264KernelSet.CreateBest"/> vs <see cref="ScalarKernelSet"/> once in
/// <see cref="GlobalSetup"/>; benchmarks call <see cref="IH264KernelSet"/> methods with no per-call dispatch.
/// </summary>
[MemoryDiagnoser]
[MinColumn, MaxColumn, MeanColumn, StdErrorColumn]
public class H264SadKernelMicroBenchmarks
{
    [ParamsAllValues]
    public bool PreferHardwareIntrinsics { get; set; }

    private byte[] _src16 = null!;
    private byte[] _ref16 = null!;
    private byte[] _src64U = null!;
    private byte[] _src64V = null!;
    private byte[] _pred64U = null!;
    private byte[] _pred64V = null!;
    private byte[] _srcMb = null!;
    private byte[] _predMb = null!;
    private byte[] _topRow9 = null!;
    private byte[] _leftCol4 = null!;
    private short[] _residual16 = null!;
    private int[] _quantBlock16 = null!;
    private int[] _quantBlock16Pristine = null!;
    private int[] _dequantInput16 = null!;

    private byte[] _topRow16 = null!;
    private byte[] _leftCol16 = null!;
    private byte _topLeft;

    private IH264KernelSet _kernels = null!;

    [GlobalSetup]
    public void Setup()
    {
        _kernels = PreferHardwareIntrinsics ? H264KernelSet.CreateBest() : new ScalarKernelSet();

        var rng = new Random(42);
        _src16 = new byte[16];
        _ref16 = new byte[16];
        rng.NextBytes(_src16);
        rng.NextBytes(_ref16);

        _src64U = new byte[64];
        _src64V = new byte[64];
        _pred64U = new byte[64];
        _pred64V = new byte[64];
        rng.NextBytes(_src64U);
        rng.NextBytes(_src64V);
        rng.NextBytes(_pred64U);
        rng.NextBytes(_pred64V);

        _srcMb = new byte[256];
        _predMb = new byte[256];
        rng.NextBytes(_srcMb);
        rng.NextBytes(_predMb);

        _topRow9 = new byte[9];
        _leftCol4 = new byte[4];
        rng.NextBytes(_topRow9);
        rng.NextBytes(_leftCol4);

        _topRow16 = new byte[16];
        _leftCol16 = new byte[16];
        rng.NextBytes(_topRow16);
        rng.NextBytes(_leftCol16);
        _topLeft = (byte)rng.Next(256);

        _residual16 = new short[16];
        for (var i = 0; i < 16; i++)
        {
            _residual16[i] = (short)(rng.Next(-256, 256));
        }

        _quantBlock16Pristine = new int[16];
        _quantBlock16 = new int[16];
        for (var i = 0; i < 16; i++)
        {
            _quantBlock16Pristine[i] = rng.Next(-2000, 2001);
        }

        _dequantInput16 = new int[16];
        for (var i = 0; i < 16; i++)
        {
            _dequantInput16[i] = rng.Next(-128, 128);
        }
    }

    /// <summary>Single 16-byte SAD via <see cref="IH264KernelSet.SadMany4x4"/> (count=1).</summary>
    [Benchmark]
    public int Sad4x4_Once()
    {
        Span<int> sad = stackalloc int[1];
        _kernels.SadMany4x4(_src16, _ref16, sad, 1);
        return sad[0];
    }

    /// <summary>16 back-to-back 4×4 SADs — mirrors the per-MB candidate-loop call shape so the
    /// dispatcher / horizontal-reduce overhead per call shows up in the measurement.</summary>
    [Benchmark]
    public int Sad4x4_x16()
    {
        var total = 0;
        Span<int> sads = stackalloc int[16];
        _kernels.SadMany4x4(_srcMb, _predMb, sads, 16);
        for (var k = 0; k < 16; k++)
        {
            total += sads[k];
        }

        return total;
    }

    /// <summary>1 source vs 9 candidate predictions via the batched dispatcher
    /// (<see cref="H264Intra4X4Simd.SadManyU8x16"/>). Mirrors the actual intra-4x4 RDO call shape
    /// after the SadMany batching change: one source load + 9 SAD reductions per block. The
    /// per-call delta vs <see cref="Sad4x4_Once"/> × 9 is the load-hoist win.</summary>
    [Benchmark]
    public int SadMany4x4_9Modes()
    {
        Span<int> sads = stackalloc int[9];
        _kernels.SadMany4x4(_src16, _predMb.AsSpan(0, 9 * 16), sads, 9);

        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += sads[i];
        }

        return sum;
    }

    /// <summary>Single 4×4 SATD via <see cref="IH264KernelSet.SatdMany4x4"/> (count=1).</summary>
    [Benchmark]
    public int Satd4x4_Once()
    {
        Span<int> satd = stackalloc int[1];
        _kernels.SatdMany4x4(_src16, _ref16, satd, 1);
        return satd[0];
    }

    /// <summary>Batched 4×4 SATD for 9 candidates, mirroring intra mode pruning shape.</summary>
    [Benchmark]
    public int SatdMany4x4_9Modes()
    {
        Span<int> satds = stackalloc int[9];
        _kernels.SatdMany4x4(_src16, _predMb.AsSpan(0, 9 * 16), satds, 9);

        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += satds[i];
        }

        return sum;
    }

    /// <summary>8×8 chroma (U + V) SAD via the dispatcher used by <c>ChooseChromaIntraMode</c>.</summary>
    [Benchmark]
    public int Chroma8x8_Sad() =>
        _kernels.SadChromaPair(_src64U, _src64V, _pred64U, _pred64V);

    /// <summary>All 9 Intra_4×4 prediction modes for one block (scalar dispatch when <see cref="_gate"/>.IntraPredict
    /// is false; gated SIMD overload when true). Mirrors the slice encoder's <c>for cand = 0..8</c> RDO loop.</summary>
    [Benchmark]
    public int Predict_4x4_AllModes()
    {
        Span<byte> dst = stackalloc byte[16];
        var checksum = 0;
        for (var mode = 0; mode < 9; mode++)
        {
            _kernels.Predict4x4(mode, _topRow9, _leftCol4, true, true, dst);
            checksum += dst[0];
        }

        return checksum;
    }

    /// <summary>Forward 4×4 DCT via resolved <see cref="IH264KernelSet"/> tier.</summary>
    [Benchmark]
    public int ForwardDct4X4()
    {
        Span<int> coeff = stackalloc int[16];
        _kernels.ForwardDct4x4(_residual16, coeff);
        return coeff[0];
    }

    /// <summary>4×4 quant via resolved tier. Reset from pristine input each call.</summary>
    [Benchmark]
    public int Quant4X4()
    {
        Span<int> block = stackalloc int[16];
        _quantBlock16Pristine.AsSpan().CopyTo(block);
        _kernels.Quant4x4(block, qp: 28);
        return block[0];
    }

    /// <summary>Approximate dequant (scalar vs SIMD static by tier).</summary>
    [Benchmark]
    public int DequantApprox4X4()
    {
        Span<int> dst = stackalloc int[16];
        if (_kernels is ScalarKernelSet)
        {
            H264BlockTransform.DequantApprox(_dequantInput16, qp: 28, dst);
        }
        else
        {
            H264BlockTransformDequantSimd.DequantApprox(_dequantInput16, qp: 28, dst);
        }

        return dst[0];
    }

    /// <summary>Inverse 4×4 DCT matrix multiply (scalar vs SIMD static by tier).</summary>
    [Benchmark]
    public int InverseDct4X4()
    {
        Span<int> residual = stackalloc int[16];
        if (_kernels is ScalarKernelSet)
        {
            H264BlockTransform.InverseDctMatrixMultiplyScalar(_dequantInput16, residual);
        }
        else
        {
            H264Dct4x4Simd.InverseDctMatrixMultiply(
                H264BlockTransform.InverseDctMatrixCoefficientsInt32, _dequantInput16, residual);
        }

        return residual[0];
    }

    /// <summary>
    /// Full residual encode + reconstruct as one bundled scalar primitive. Direct delta vs
    /// <see cref="EncodeResidual4x4_PerKernelChain"/> is the bundling win at the per-block level.
    /// </summary>
    [Benchmark]
    public int EncodeResidual4x4_Bundled()
    {
        Span<short> zigzag = stackalloc short[16];
        Span<byte> recon = stackalloc byte[16];
        return H264TransformBundle.EncodeResidual4x4Scalar(_src16, _ref16, qp: 28, zigzag, recon, recStride: 4);
    }

    /// <summary>SIMD fused bundle (DCT + quant + dequant + IDCT); compare mean vs <see cref="EncodeResidual4x4_Bundled"/>.</summary>
    [Benchmark]
    public int EncodeResidual4x4_BundledSimd()
    {
        Span<short> zigzag = stackalloc short[16];
        Span<byte> recon = stackalloc byte[16];
        return H264TransformBundle.EncodeResidual4x4Simd(_src16, _ref16, qp: 28, zigzag, recon, recStride: 4);
    }

    /// <summary>
    /// Replicates the pre-bundle per-block chain that <c>WriteMacroblock</c> ran (residual ->
    /// ForwardDct -> Quant -> RasterToZigzag -> CopyZigzagToShort -> nnz -> ZigzagToRaster ->
    /// Dequant -> InverseDct -> reconstruct). Scalar-only so the delta against
    /// <see cref="EncodeResidual4x4_Bundled"/> isolates the call-boundary / temp-buffer / bounds-
    /// check overhead the bundling removes.
    /// </summary>
    [Benchmark]
    public int EncodeResidual4x4_PerKernelChain()
    {
        Span<short> residual = stackalloc short[16];
        for (var i = 0; i < 16; i++)
        {
            residual[i] = (short)(_src16[i] - _ref16[i]);
        }

        Span<int> coeff = stackalloc int[16];
        H264BlockTransform.ForwardDct4X4Scalar(residual, coeff);
        H264BlockTransform.Quant4X4Scalar(coeff, 28);

        Span<int> zz = stackalloc int[16];
        H264BlockTransform.RasterToZigzag(coeff, zz);
        Span<short> zzS = stackalloc short[16];
        H264BlockTransform.CopyZigzagToShort(zz, zzS);
        var nz = 0;
        for (var i = 0; i < 16; i++)
        {
            if (zzS[i] != 0)
            {
                nz++;
            }
        }

        H264BlockTransform.ZigzagToRaster(zz, coeff);
        Span<int> dequant = stackalloc int[16];
        H264BlockTransform.DequantApprox(coeff, 28, dequant);
        Span<int> invRes = stackalloc int[16];
        H264BlockTransform.InverseDctMatrixMultiplyScalar(dequant, invRes);

        Span<byte> recon = stackalloc byte[16];
        for (var rr = 0; rr < 4; rr++)
        {
            for (var cc = 0; cc < 4; cc++)
            {
                recon[rr * 4 + cc] = (byte)Math.Clamp(_ref16[rr * 4 + cc] + invRes[rr * 4 + cc], 0, 255);
            }
        }

        return nz;
    }

    /// <summary>
    /// All four Intra_16×16 luma prediction modes — mirrors the per-MB mode competition shape
    /// (<c>BestI16x16Mode</c> evaluate-and-predict loop). Dispatches to SIMD or scalar depending
    /// on <see cref="PreferHardwareIntrinsics"/>. Pairs with <see cref="Intra16x16Sad_OneMb"/> to
    /// measure the predict-only vs SAD-only contribution to the overall 16×16 budget.
    /// </summary>
    [Benchmark]
    public int Intra16x16Predict_AllModes()
    {
        Span<byte> dst = stackalloc byte[256];
        var checksum = 0;
        // mode 0 (V): needs top
        _kernels.PredictIntra16x16(0, _topRow16, topAvail: true, _leftCol16, leftAvail: true,
            _topLeft, topLeftAvail: true, dst);
        checksum += dst[0];
        _kernels.PredictIntra16x16(1, _topRow16, topAvail: true, _leftCol16, leftAvail: true,
            _topLeft, topLeftAvail: true, dst);
        checksum += dst[0];
        _kernels.PredictIntra16x16(2, _topRow16, topAvail: true, _leftCol16, leftAvail: true,
            _topLeft, topLeftAvail: true, dst);
        checksum += dst[0];
        _kernels.PredictIntra16x16(3, _topRow16, topAvail: true, _leftCol16, leftAvail: true,
            _topLeft, topLeftAvail: true, dst);
        checksum += dst[0];
        return checksum;
    }

    /// <summary>
    /// 16×16 luma SAD for one macroblock — the inner reduction called four times per MB in
    /// <c>BestI16x16Mode</c>. SIMD path is <see cref="H264Intra16x16PredictionSimd.Sad16x16"/>
    /// when <see cref="PreferHardwareIntrinsics"/>, scalar otherwise.
    /// </summary>
    [Benchmark]
    public int Intra16x16Sad_OneMb() =>
        _kernels.SadIntra16x16(_srcMb, _predMb, srcStride: 16);

    // ── Deblocking filter micro-benchmark (Phase 5) ───────────────────────────────────────────────

    private const int DeblockMbW = 2;
    private const int DeblockMbH = 2;
    private const int DeblockStride = DeblockMbW * 16;
    private const int DeblockChromaStride = DeblockMbW * 8;

    private byte[] _deblockYSrc = null!;
    private byte[] _deblockY = null!;
    private byte[] _deblockU = null!;
    private byte[] _deblockV = null!;
    private byte[] _deblockBsH = null!;
    private byte[] _deblockBsV = null!;
    private int[] _deblockQpY = null!;
    private int[] _deblockQpUv = null!;

    [GlobalSetup(Targets = new[] { nameof(DeblockLuma_IntraFrame) })]
    public void SetupDeblock()
    {
        _kernels = PreferHardwareIntrinsics ? H264KernelSet.CreateBest() : new ScalarKernelSet();

        const int lumaSize = DeblockStride * DeblockMbH * 16;
        const int chromaSize = DeblockChromaStride * DeblockMbH * 8;

        var rng = new Random(123);
        _deblockYSrc = new byte[lumaSize];
        _deblockY = new byte[lumaSize];
        _deblockU = new byte[chromaSize];
        _deblockV = new byte[chromaSize];
        rng.NextBytes(_deblockYSrc);
        rng.NextBytes(_deblockU);
        rng.NextBytes(_deblockV);

        const int totalBs = DeblockMbW * DeblockMbH * 16;
        _deblockBsH = new byte[totalBs];
        _deblockBsV = new byte[totalBs];

        // All-intra frame pattern: outer MB edges bs=4, internal edges bs=3.
        for (var mb = 0; mb < DeblockMbW * DeblockMbH; mb++)
        {
            for (var i = 0; i < 16; i++)
            {
                _deblockBsH[mb * 16 + i] = i < 4 ? (byte)4 : (byte)3;
                _deblockBsV[mb * 16 + i] = i < 4 ? (byte)4 : (byte)3;
            }
        }

        _deblockQpY = Enumerable.Repeat(28, DeblockMbW * DeblockMbH).ToArray();
        _deblockQpUv = Enumerable.Repeat(28, DeblockMbW * DeblockMbH).ToArray();
    }

    /// <summary>
    /// Isolated deblocking filter benchmark over a 2×2-MB Y/U/V triplet simulating an all-intra
    /// frame (bs=3 internal, bs=4 outer). Toggle <see cref="PreferHardwareIntrinsics"/> to compare
    /// SIMD vs scalar throughput; the luma loop is the vectorized path.
    /// </summary>
    [Benchmark]
    public void DeblockLuma_IntraFrame()
    {
        _deblockYSrc.CopyTo(_deblockY, 0);
        _kernels.ApplyDeblock(
            _deblockY, DeblockStride,
            _deblockU, _deblockV, DeblockChromaStride,
            DeblockMbW, DeblockMbH,
            _deblockBsH, _deblockBsV,
            _deblockQpY, _deblockQpUv,
            alphaOffsetDiv2: 0, betaOffsetDiv2: 0);
    }

    public static void RunAll(string[] args) =>
        BenchmarkDotNet.Running.BenchmarkSwitcher.FromAssembly(typeof(H264SadKernelMicroBenchmarks).Assembly).Run(args);
}
