namespace Kiln.Internal.H264;

internal sealed class ScalarKernelSet : IH264KernelSet
{
    public int Sad16x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        H264MotionSad.Sad16x16Scalar(a, strideA, b, strideB);

    public int Sad16x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        H264MotionSad.Sad16x8Scalar(a, strideA, b, strideB);

    public int Sad8x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        H264MotionSad.Sad8x16Scalar(a, strideA, b, strideB);

    public int Sad8x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        H264MotionSad.Sad8x8Scalar(a, strideA, b, strideB);

    public int Ssd16x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        H264MotionSsd.Ssd16x16Scalar(a, strideA, b, strideB);

    public int Ssd8x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        H264MotionSsd.Ssd8x8Scalar(a, strideA, b, strideB);

    public void SadMany4x4(ReadOnlySpan<byte> src, ReadOnlySpan<byte> predConcat, Span<int> sads, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var off = i * 16;
            var s = 0;
            for (var k = 0; k < 16; k++)
            {
                s += Math.Abs(src[k] - predConcat[off + k]);
            }

            sads[i] = s;
        }
    }

    public int SadIntra16x16(ReadOnlySpan<byte> src256, ReadOnlySpan<byte> pred256, int srcStride)
    {
        var sad = 0;
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                sad += Math.Abs(src256[y * srcStride + x] - pred256[y * 16 + x]);
            }
        }

        return sad;
    }

    public int SadChromaPair(ReadOnlySpan<byte> srcU, ReadOnlySpan<byte> srcV, ReadOnlySpan<byte> predU, ReadOnlySpan<byte> predV)
    {
        var sad = 0;
        for (var i = 0; i < 64; i++)
        {
            sad += Math.Abs(srcU[i] - predU[i]);
            sad += Math.Abs(srcV[i] - predV[i]);
        }

        return sad;
    }

    public int Satd16x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        H264MotionSatd.Satd16x16Scalar(a, strideA, b, strideB);

    public int Satd16x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        H264MotionSatd.Satd16x8Scalar(a, strideA, b, strideB);

    public int Satd8x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        H264MotionSatd.Satd8x16Scalar(a, strideA, b, strideB);

    public int Satd8x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        H264MotionSatd.Satd8x8Scalar(a, strideA, b, strideB);

    public void SatdMany4x4(ReadOnlySpan<byte> src, ReadOnlySpan<byte> predConcat, Span<int> satds, int count) =>
        H264MotionSatd.SatdMany4x4Scalar(src, predConcat, satds, count);

    public void Predict4x4(int mode, ReadOnlySpan<byte> topRow, ReadOnlySpan<byte> leftCol, bool topAvail, bool leftAvail, Span<byte> dst16) =>
        H264Intra4X4Prediction.Predict(mode, topRow, leftCol, topAvail, leftAvail, dst16);

    public void PredictIntra16x16(int mode, ReadOnlySpan<byte> topRow, bool topAvail, ReadOnlySpan<byte> leftCol, bool leftAvail, byte topLeft, bool topLeftAvail, Span<byte> dst256) =>
        H264Intra16x16Prediction.PredictScalar(mode, topRow, topAvail, leftCol, leftAvail, topLeft, topLeftAvail, dst256);

    public int EncodeResidual4x4(ReadOnlySpan<byte> src16, ReadOnlySpan<byte> pred16, int qp, Span<short> zigzagCoeffOut16, Span<byte> recDst, int recStride) =>
        H264TransformBundle.EncodeResidual4x4Scalar(src16, pred16, qp, zigzagCoeffOut16, recDst, recStride);

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
        H264DeblockingFilter.ApplyScalar(y, strideY, u, v, strideUv, mbWidth, mbHeight, bsHorizontal, bsVertical, qpY, qpUv, alphaOffsetDiv2, betaOffsetDiv2);

    public void InterpolateLuma(
        ReadOnlySpan<byte> src, int srcStride,
        int srcOriginX, int srcOriginY,
        int xFrac, int yFrac,
        int blockWidth, int blockHeight,
        Span<byte> dst, int dstStride) =>
        H264QpelLumaInterp.Interpolate(src, srcStride, srcOriginX, srcOriginY, xFrac, yFrac, blockWidth, blockHeight, dst, dstStride);

    public void InterpolateChroma(
        ReadOnlySpan<byte> src, int srcStride,
        int srcOriginX, int srcOriginY,
        int xFrac, int yFrac,
        int blockWidth, int blockHeight,
        Span<byte> dst, int dstStride) =>
        H264BilinearChromaInterp.Interpolate(src, srcStride, srcOriginX, srcOriginY, xFrac, yFrac, blockWidth, blockHeight, dst, dstStride);

    public void GatherSrcBlock4x4(ReadOnlySpan<byte> srcY, int baseOff, int strideY, Span<byte> dst16) =>
        H264SrcGather.GatherSrcBlock4x4Scalar(srcY, baseOff, strideY, dst16);

    public void GatherChroma8x8(ReadOnlySpan<byte> src, int stride, int bx, int by, Span<byte> dst64) =>
        H264SrcGather.GatherChroma8x8(src, stride, bx, by, dst64);

    public void ForwardDct4x4(ReadOnlySpan<short> residual4x4, Span<int> outCoeff) =>
        H264BlockTransform.ForwardDct4X4Scalar(residual4x4, outCoeff);

    public void Quant4x4(Span<int> block, int qp) =>
        H264BlockTransform.Quant4X4Scalar(block, qp);
}
