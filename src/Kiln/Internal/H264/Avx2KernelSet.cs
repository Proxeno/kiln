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

    // Sad16x16x4 stays on the inherited SSE2 kernel pending an on-host comparison against
    // H264MotionSad.Sad16x16x4Avx2; Sad8x8x4 packs four rows per 256-bit vector, halving the
    // number of psadbw instructions.
    public override void Sad8x8x4(
        ReadOnlySpan<byte> a, int strideA,
        ReadOnlySpan<byte> b0, ReadOnlySpan<byte> b1,
        ReadOnlySpan<byte> b2, ReadOnlySpan<byte> b3,
        int strideB, Span<int> sads) =>
        H264MotionSad.Sad8x8x4Avx2(a, strideA, b0, b1, b2, b3, strideB, sads);
}
