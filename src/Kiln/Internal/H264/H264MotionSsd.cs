using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Kiln.Internal.H264;

/// <summary>
/// Sum-of-squared-differences (prediction SSE) between a source block and an inter prediction —
/// the distortion metric behind the Phase-1 P_Skip acceptance gate in
/// <see cref="H264BaselineSliceEncoder"/>. Encoder-private: not defined by ITU-T H.264; it only
/// feeds encoder-side threshold compares. Every path accumulates exact integer squares, so all
/// ISAs return the bit-identical scalar sum and kernel choice can never change an encoding
/// decision.
/// </summary>
/// <remarks>
/// Strides for <c>a</c> and <c>b</c> may differ — the source block is strided within its plane
/// while the prediction buffer is a contiguous 16- or 8-wide block. Accumulators cannot overflow:
/// the worst case (16×16, every sample differing by 255) sums to 256 · 255² &lt; 2³¹, and per-lane
/// partial sums are bounded by that same total.
/// </remarks>
internal static class H264MotionSsd
{
    /// <summary>Scalar reference SSD over a 16×16 block (exposed for SIMD parity testing).</summary>
    public static int Ssd16x16Scalar(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        SsdScalarNxM(a, strideA, b, strideB, width: 16, height: 16);

    /// <summary>Scalar reference SSD over an 8×8 block (exposed for SIMD parity testing).</summary>
    public static int Ssd8x8Scalar(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        SsdScalarNxM(a, strideA, b, strideB, width: 8, height: 8);

    /// <summary>NEON SSD over a 16×16 block: UABDL/UABDL2 then UMLAL/UMLAL2 square-accumulate.</summary>
    internal static int Ssd16x16AdvSimd(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        ref var ra = ref MemoryMarshal.GetReference(a);
        ref var rb = ref MemoryMarshal.GetReference(b);
        var acc = Vector128<uint>.Zero;
        for (var y = 0; y < 16; y++)
        {
            ref var rowA = ref Unsafe.Add(ref ra, y * strideA);
            ref var rowB = ref Unsafe.Add(ref rb, y * strideB);
            var va = Unsafe.ReadUnaligned<Vector128<byte>>(ref rowA);
            var vb = Unsafe.ReadUnaligned<Vector128<byte>>(ref rowB);
            var dLo = AdvSimd.AbsoluteDifferenceWideningLower(va.GetLower(), vb.GetLower());
            var dHi = AdvSimd.AbsoluteDifferenceWideningUpper(va, vb);
            acc = AdvSimd.MultiplyWideningLowerAndAdd(acc, dLo.GetLower(), dLo.GetLower());
            acc = AdvSimd.MultiplyWideningUpperAndAdd(acc, dLo, dLo);
            acc = AdvSimd.MultiplyWideningLowerAndAdd(acc, dHi.GetLower(), dHi.GetLower());
            acc = AdvSimd.MultiplyWideningUpperAndAdd(acc, dHi, dHi);
        }

        return ReduceUintAccumulator(acc);
    }

    /// <summary>NEON SSD over an 8×8 block (see <see cref="Ssd16x16AdvSimd"/>).</summary>
    internal static int Ssd8x8AdvSimd(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        ref var ra = ref MemoryMarshal.GetReference(a);
        ref var rb = ref MemoryMarshal.GetReference(b);
        var acc = Vector128<uint>.Zero;
        for (var y = 0; y < 8; y++)
        {
            ref var rowA = ref Unsafe.Add(ref ra, y * strideA);
            ref var rowB = ref Unsafe.Add(ref rb, y * strideB);
            var va = Unsafe.ReadUnaligned<Vector64<byte>>(ref rowA);
            var vb = Unsafe.ReadUnaligned<Vector64<byte>>(ref rowB);
            var d = AdvSimd.AbsoluteDifferenceWideningLower(va, vb);
            acc = AdvSimd.MultiplyWideningLowerAndAdd(acc, d.GetLower(), d.GetLower());
            acc = AdvSimd.MultiplyWideningUpperAndAdd(acc, d, d);
        }

        return ReduceUintAccumulator(acc);
    }

    /// <summary>SSE2-tier SSD over a 16×16 block: zero-extend to i16, subtract, PMADDWD accumulate.</summary>
    internal static int Ssd16x16Ssse3(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        ref var ra = ref MemoryMarshal.GetReference(a);
        ref var rb = ref MemoryMarshal.GetReference(b);
        var acc = Vector128<int>.Zero;
        var zero = Vector128<byte>.Zero;
        for (var y = 0; y < 16; y++)
        {
            ref var rowA = ref Unsafe.Add(ref ra, y * strideA);
            ref var rowB = ref Unsafe.Add(ref rb, y * strideB);
            var va = Unsafe.ReadUnaligned<Vector128<byte>>(ref rowA);
            var vb = Unsafe.ReadUnaligned<Vector128<byte>>(ref rowB);
            var dLo = Sse2.Subtract(Sse2.UnpackLow(va, zero).AsInt16(), Sse2.UnpackLow(vb, zero).AsInt16());
            var dHi = Sse2.Subtract(Sse2.UnpackHigh(va, zero).AsInt16(), Sse2.UnpackHigh(vb, zero).AsInt16());
            acc = Sse2.Add(acc, Sse2.MultiplyAddAdjacent(dLo, dLo));
            acc = Sse2.Add(acc, Sse2.MultiplyAddAdjacent(dHi, dHi));
        }

        return acc.GetElement(0) + acc.GetElement(1) + acc.GetElement(2) + acc.GetElement(3);
    }

    /// <summary>SSE2-tier SSD over an 8×8 block (see <see cref="Ssd16x16Ssse3"/>).</summary>
    internal static int Ssd8x8Ssse3(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        ref var ra = ref MemoryMarshal.GetReference(a);
        ref var rb = ref MemoryMarshal.GetReference(b);
        var acc = Vector128<int>.Zero;
        var zero = Vector128<byte>.Zero;
        for (var y = 0; y < 8; y++)
        {
            ref var rowA = ref Unsafe.Add(ref ra, y * strideA);
            ref var rowB = ref Unsafe.Add(ref rb, y * strideB);
            var va = Vector128.Create(Unsafe.ReadUnaligned<ulong>(ref rowA), 0UL).AsByte();
            var vb = Vector128.Create(Unsafe.ReadUnaligned<ulong>(ref rowB), 0UL).AsByte();
            var d = Sse2.Subtract(Sse2.UnpackLow(va, zero).AsInt16(), Sse2.UnpackLow(vb, zero).AsInt16());
            acc = Sse2.Add(acc, Sse2.MultiplyAddAdjacent(d, d));
        }

        return acc.GetElement(0) + acc.GetElement(1) + acc.GetElement(2) + acc.GetElement(3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SsdScalarNxM(
        ReadOnlySpan<byte> a, int strideA,
        ReadOnlySpan<byte> b, int strideB,
        int width, int height)
    {
        var s = 0;
        for (var y = 0; y < height; y++)
        {
            var oa = y * strideA;
            var ob = y * strideB;
            for (var x = 0; x < width; x++)
            {
                var d = a[oa + x] - b[ob + x];
                s += d * d;
            }
        }

        return s;
    }

    /// <summary>
    /// Single horizontal reduction of a Vector128&lt;uint&gt; SSD accumulator. Uses one ADDV on
    /// AArch64; falls back to a scalar lane sum on 32-bit Arm where Arm64 intrinsics are
    /// unavailable.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReduceUintAccumulator(Vector128<uint> acc)
    {
        if (AdvSimd.Arm64.IsSupported)
        {
            return (int)AdvSimd.Arm64.AddAcross(acc).ToScalar();
        }

        return (int)(acc.GetElement(0) + acc.GetElement(1) + acc.GetElement(2) + acc.GetElement(3));
    }
}
