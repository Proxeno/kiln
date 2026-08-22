namespace Kiln.Internal.H264;

/// <summary>Shared SIMD <see cref="IH264KernelSet"/> implementations; platform tiers override SAD/SATD entry points only.</summary>
internal abstract class SimdKernelSetBase : IH264KernelSet
{
    public abstract int Sad16x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB);
    public abstract int Sad16x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB);
    public abstract int Sad8x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB);
    public abstract int Sad8x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB);

    public abstract void SadMany4x4(ReadOnlySpan<byte> src, ReadOnlySpan<byte> predConcat, Span<int> sads, int count);

    public abstract int SadIntra16x16(ReadOnlySpan<byte> src256, ReadOnlySpan<byte> pred256, int srcStride);

    public abstract int SadChromaPair(ReadOnlySpan<byte> srcU, ReadOnlySpan<byte> srcV, ReadOnlySpan<byte> predU, ReadOnlySpan<byte> predV);

    public int Satd16x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        H264MotionSatd.Satd16x16Simd(a, strideA, b, strideB);

    public int Satd16x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        H264MotionSatd.Satd16x8Simd(a, strideA, b, strideB);

    public int Satd8x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        H264MotionSatd.Satd8x16Simd(a, strideA, b, strideB);

    public int Satd8x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        H264MotionSatd.Satd8x8Simd(a, strideA, b, strideB);

    public void SatdMany4x4(ReadOnlySpan<byte> src, ReadOnlySpan<byte> predConcat, Span<int> satds, int count) =>
        H264MotionSatd.SatdMany4x4Simd(src, predConcat, satds, count);

    public void Predict4x4(int mode, ReadOnlySpan<byte> topRow, ReadOnlySpan<byte> leftCol, bool topAvail, bool leftAvail, Span<byte> dst16) =>
        H264Intra4X4Prediction.PredictSimdDirect(mode, topRow, leftCol, topAvail, leftAvail, dst16);

    public void PredictIntra16x16(int mode, ReadOnlySpan<byte> topRow, bool topAvail, ReadOnlySpan<byte> leftCol, bool leftAvail, byte topLeft, bool topLeftAvail, Span<byte> dst256) =>
        H264Intra16x16Prediction.PredictSimd(mode, topRow, topAvail, leftCol, leftAvail, topLeft, topLeftAvail, dst256);

    public int EncodeResidual4x4(ReadOnlySpan<byte> src16, ReadOnlySpan<byte> pred16, int qp, Span<short> zigzagCoeffOut16, Span<byte> recDst, int recStride) =>
        H264TransformBundle.EncodeResidual4x4Simd(src16, pred16, qp, zigzagCoeffOut16, recDst, recStride);

    public void ApplyDeblock(
        Span<byte> y, int strideY,
        Span<byte> u, Span<byte> v, int strideUv,
        int mbWidth, int mbHeight,
        ReadOnlySpan<byte> bsHorizontal,
        ReadOnlySpan<byte> bsVertical,
        ReadOnlySpan<int> qpY,
        ReadOnlySpan<int> qpUv,
        int alphaOffsetDiv2,
        int betaOffsetDiv2) =>
        H264DeblockingFilter.ApplySimd(y, strideY, u, v, strideUv, mbWidth, mbHeight, bsHorizontal, bsVertical, qpY, qpUv, alphaOffsetDiv2, betaOffsetDiv2);

    public void InterpolateLuma(
        ReadOnlySpan<byte> src, int srcStride,
        int srcOriginX, int srcOriginY,
        int xFrac, int yFrac,
        int blockWidth, int blockHeight,
        Span<byte> dst, int dstStride) =>
        H264QpelLumaInterp.InterpolateSimd(src, srcStride, srcOriginX, srcOriginY, xFrac, yFrac, blockWidth, blockHeight, dst, dstStride);

    public void InterpolateChroma(
        ReadOnlySpan<byte> src, int srcStride,
        int srcOriginX, int srcOriginY,
        int xFrac, int yFrac,
        int blockWidth, int blockHeight,
        Span<byte> dst, int dstStride) =>
        H264BilinearChromaInterp.InterpolateSimd(src, srcStride, srcOriginX, srcOriginY, xFrac, yFrac, blockWidth, blockHeight, dst, dstStride);

    public void GatherSrcBlock4x4(ReadOnlySpan<byte> srcY, int baseOff, int strideY, Span<byte> dst16) =>
        H264SrcGather.GatherSrcBlock4x4Simd(srcY, baseOff, strideY, dst16);

    public void GatherChroma8x8(ReadOnlySpan<byte> src, int stride, int bx, int by, Span<byte> dst64) =>
        H264SrcGather.GatherChroma8x8(src, stride, bx, by, dst64);

    public void ForwardDct4x4(ReadOnlySpan<short> residual4x4, Span<int> outCoeff) =>
        H264Dct4x4Simd.ForwardDct4X4(residual4x4, outCoeff);

    public void Quant4x4(Span<int> block, int qp) =>
        H264BlockTransformSimd.Quant4X4(block, qp);
}
