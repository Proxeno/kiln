namespace Kiln.Internal.H264;

internal class Ssse3KernelSet : SimdKernelSetBase
{
    public override int Sad16x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        H264MotionSad.Sad16x16Ssse3(a, strideA, b, strideB);

    public override int Sad16x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        H264MotionSad.Sad16x8Ssse3(a, strideA, b, strideB);

    public override int Sad8x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        H264MotionSad.Sad8x16Ssse3(a, strideA, b, strideB);

    public override int Sad8x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        H264MotionSad.Sad8x8Ssse3(a, strideA, b, strideB);

    public override void Sad16x16x4(
        ReadOnlySpan<byte> a, int strideA,
        ReadOnlySpan<byte> b0, ReadOnlySpan<byte> b1,
        ReadOnlySpan<byte> b2, ReadOnlySpan<byte> b3,
        int strideB, Span<int> sads) =>
        H264MotionSad.Sad16x16x4Sse2(a, strideA, b0, b1, b2, b3, strideB, sads);

    public override void Sad8x8x4(
        ReadOnlySpan<byte> a, int strideA,
        ReadOnlySpan<byte> b0, ReadOnlySpan<byte> b1,
        ReadOnlySpan<byte> b2, ReadOnlySpan<byte> b3,
        int strideB, Span<int> sads) =>
        H264MotionSad.Sad8x8x4Sse2(a, strideA, b0, b1, b2, b3, strideB, sads);

    public override void SadMany4x4(ReadOnlySpan<byte> src, ReadOnlySpan<byte> predConcat, Span<int> sads, int count) =>
        H264Intra4X4Simd.SadManySsse3(src, predConcat, sads, count);

    public override int SadIntra16x16(ReadOnlySpan<byte> src256, ReadOnlySpan<byte> pred256, int srcStride) =>
        H264Intra16x16PredictionSimd.Sad16x16Ssse2(src256, pred256, srcStride);

    public override int SadChromaPair(ReadOnlySpan<byte> srcU, ReadOnlySpan<byte> srcV, ReadOnlySpan<byte> predU, ReadOnlySpan<byte> predV) =>
        H264ChromaSadSimd.SadU8x8PairSsse2(srcU, srcV, predU, predV);

    public override int Ssd16x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        H264MotionSsd.Ssd16x16Ssse3(a, strideA, b, strideB);

    public override int Ssd8x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        H264MotionSsd.Ssd8x8Ssse3(a, strideA, b, strideB);

    public override int VarianceMb16x16(ReadOnlySpan<byte> mbTopLeft, int stride) =>
        H264VarianceFastPath.VarianceMb16x16Ssse3(mbTopLeft, stride);
}
