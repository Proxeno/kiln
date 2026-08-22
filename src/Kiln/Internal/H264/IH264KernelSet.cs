namespace Kiln.Internal.H264;

/// <summary>
/// Resolved H.264 encoder hot-path kernels for one encoder instance. Picked once at construction;
/// no per-slice or per-MB ISA dispatch.
/// </summary>
internal interface IH264KernelSet
{
    int Sad16x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB);
    int Sad16x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB);
    int Sad8x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB);
    int Sad8x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB);

    void SadMany4x4(ReadOnlySpan<byte> src, ReadOnlySpan<byte> predConcat, Span<int> sads, int count);
    int SadIntra16x16(ReadOnlySpan<byte> src256, ReadOnlySpan<byte> pred256, int srcStride);
    int SadChromaPair(ReadOnlySpan<byte> srcU, ReadOnlySpan<byte> srcV, ReadOnlySpan<byte> predU, ReadOnlySpan<byte> predV);

    int Satd16x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB);
    int Satd16x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB);
    int Satd8x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB);
    int Satd8x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB);
    void SatdMany4x4(ReadOnlySpan<byte> src, ReadOnlySpan<byte> predConcat, Span<int> satds, int count);

    void Predict4x4(int mode, ReadOnlySpan<byte> topRow, ReadOnlySpan<byte> leftCol, bool topAvail, bool leftAvail, Span<byte> dst16);
    void PredictIntra16x16(int mode, ReadOnlySpan<byte> topRow, bool topAvail, ReadOnlySpan<byte> leftCol, bool leftAvail, byte topLeft, bool topLeftAvail, Span<byte> dst256);

    int EncodeResidual4x4(ReadOnlySpan<byte> src16, ReadOnlySpan<byte> pred16, int qp, Span<short> zigzagCoeffOut16, Span<byte> recDst, int recStride);

    void ApplyDeblock(
        Span<byte> y, int strideY,
        Span<byte> u, Span<byte> v, int strideUv,
        int mbWidth, int mbHeight,
        ReadOnlySpan<byte> bsHorizontal,
        ReadOnlySpan<byte> bsVertical,
        ReadOnlySpan<int> qpY,
        ReadOnlySpan<int> qpUv,
        int alphaOffsetDiv2,
        int betaOffsetDiv2);

    void InterpolateLuma(
        ReadOnlySpan<byte> src, int srcStride,
        int srcOriginX, int srcOriginY,
        int xFrac, int yFrac,
        int blockWidth, int blockHeight,
        Span<byte> dst, int dstStride);

    void InterpolateChroma(
        ReadOnlySpan<byte> src, int srcStride,
        int srcOriginX, int srcOriginY,
        int xFrac, int yFrac,
        int blockWidth, int blockHeight,
        Span<byte> dst, int dstStride);

    void GatherSrcBlock4x4(ReadOnlySpan<byte> srcY, int baseOff, int strideY, Span<byte> dst16);

    void GatherChroma8x8(ReadOnlySpan<byte> src, int stride, int bx, int by, Span<byte> dst64);

    void ForwardDct4x4(ReadOnlySpan<short> residual4x4, Span<int> outCoeff);

    void Quant4x4(Span<int> block, int qp);
}
