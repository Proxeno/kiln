namespace Kiln.Internal.H264;

internal class NeonKernelSet : SimdKernelSetBase
{
    public override int Sad16x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        H264MotionSad.Sad16x16AdvSimd(a, strideA, b, strideB);

    public override int Sad16x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        H264MotionSad.Sad16x8AdvSimd(a, strideA, b, strideB);

    public override int Sad8x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        H264MotionSad.Sad8x16AdvSimd(a, strideA, b, strideB);

    public override int Sad8x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        H264MotionSad.Sad8x8AdvSimd(a, strideA, b, strideB);

    public override void SadMany4x4(ReadOnlySpan<byte> src, ReadOnlySpan<byte> predConcat, Span<int> sads, int count) =>
        H264Intra4X4Simd.SadManyAdvSimd(src, predConcat, sads, count);

    public override int SadIntra16x16(ReadOnlySpan<byte> src256, ReadOnlySpan<byte> pred256, int srcStride) =>
        H264Intra16x16PredictionSimd.Sad16x16(src256, pred256, srcStride);

    public override int SadChromaPair(ReadOnlySpan<byte> srcU, ReadOnlySpan<byte> srcV, ReadOnlySpan<byte> predU, ReadOnlySpan<byte> predV) =>
        H264ChromaSadSimd.SadU8x8PairAdvSimd(srcU, srcV, predU, predV);
}
