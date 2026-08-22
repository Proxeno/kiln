namespace Kiln.Internal.H264;

internal sealed class Neon64KernelSet : NeonKernelSet
{
    public override void SadMany4x4(ReadOnlySpan<byte> src, ReadOnlySpan<byte> predConcat, Span<int> sads, int count) =>
        H264Intra4X4Simd.SadManyAdvSimdArm64(src, predConcat, sads, count);

    public override int SadIntra16x16(ReadOnlySpan<byte> src256, ReadOnlySpan<byte> pred256, int srcStride) =>
        H264Intra16x16PredictionSimd.Sad16x16Neon64(src256, pred256, srcStride);
}
