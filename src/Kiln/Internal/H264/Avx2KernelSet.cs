namespace Kiln.Internal.H264;

internal sealed class Avx2KernelSet : Ssse3KernelSet
{
    public override int Sad16x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        H264MotionSad.Sad16x16Avx2(a, strideA, b, strideB);

    public override int Sad16x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        H264MotionSad.Sad16x8Avx2(a, strideA, b, strideB);

    public override int Sad8x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        H264MotionSad.Sad8x16Avx2(a, strideA, b, strideB);

    public override int Sad8x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        H264MotionSad.Sad8x8Avx2(a, strideA, b, strideB);
}
