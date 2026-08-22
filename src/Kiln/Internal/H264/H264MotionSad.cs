using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Kiln.Internal.H264;

/// <summary>
/// Multi-block-size sum-of-absolute-differences (SAD) for inter motion estimation. Encoder-private:
/// not defined by ITU-T H.264; used as a distortion metric when searching motion vectors (see 8.4 in
/// general terms for inter prediction, which references reconstructed samples but not SAD itself).
/// </summary>
/// <remarks>
/// Strides for <c>a</c> and <c>b</c> may differ — motion search compares a current block against
/// shifted reference patches with their own stride.
/// Dispatch honours <see cref="H264IntrinsicsPreference.UseMotionSadSimd"/> so
/// <see cref="H264IntrinsicsPreference.PreferIntrinsics"/> false round-trips through scalar for parity tests.
/// On x64, 16-wide rows use AVX2 <c>vpsadbw</c> when <see cref="Avx2.IsSupported"/>, else SSSE3.
/// </remarks>
internal static class H264MotionSad
{
    /// <summary>
    /// True when an accelerated SIMD path is available (x86 SSSE3 tier with optional AVX2 dispatch, or ARM NEON).
    /// </summary>
    public static bool IsSupported => Ssse3.IsSupported || AdvSimd.IsSupported;

    /// <summary>
    /// True when the AVX2 motion-SAD path is available on this host (hardware only; ignores
    /// <see cref="H264IntrinsicsPreference.PreferIntrinsics"/>).
    /// </summary>
    internal static bool IsAvx2MotionSadSupported => Avx2.IsSupported;

    /// <summary>SAD over a 16×16 block (dispatches SIMD if <see cref="H264IntrinsicsPreference.UseMotionSadSimd"/>, else scalar).</summary>
    public static int Sad16x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        if (H264IntrinsicsPreference.UseMotionSadSimd)
        {
            return Sad16xNIntrinsics(a, strideA, b, strideB, height: 16);
        }

        return Sad16x16Scalar(a, strideA, b, strideB);
    }

    /// <summary>SAD over a 16×8 block.</summary>
    public static int Sad16x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        if (H264IntrinsicsPreference.UseMotionSadSimd)
        {
            return Sad16xNIntrinsics(a, strideA, b, strideB, height: 8);
        }

        return Sad16x8Scalar(a, strideA, b, strideB);
    }

    /// <summary>SAD over an 8×16 block.</summary>
    public static int Sad8x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        if (H264IntrinsicsPreference.UseMotionSadSimd)
        {
            return Sad8xNIntrinsics(a, strideA, b, strideB, height: 16);
        }

        return Sad8x16Scalar(a, strideA, b, strideB);
    }

    /// <summary>SAD over an 8×8 block.</summary>
    public static int Sad8x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        if (H264IntrinsicsPreference.UseMotionSadSimd)
        {
            return Sad8xNIntrinsics(a, strideA, b, strideB, height: 8);
        }

        return Sad8x8Scalar(a, strideA, b, strideB);
    }

    /// <summary>Scalar reference SAD over a 16×16 block (exposed for SIMD parity testing).</summary>
    public static int Sad16x16Scalar(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        return SadScalarNxM(a, strideA, b, strideB, width: 16, height: 16);
    }

    /// <summary>Scalar reference SAD over a 16×8 block.</summary>
    public static int Sad16x8Scalar(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        return SadScalarNxM(a, strideA, b, strideB, width: 16, height: 8);
    }

    /// <summary>Scalar reference SAD over an 8×16 block.</summary>
    public static int Sad8x16Scalar(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        return SadScalarNxM(a, strideA, b, strideB, width: 8, height: 16);
    }

    /// <summary>Scalar reference SAD over an 8×8 block.</summary>
    public static int Sad8x8Scalar(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        return SadScalarNxM(a, strideA, b, strideB, width: 8, height: 8);
    }

    /// <summary>16×N SAD via AVX2 only (parity tests). Requires <see cref="Avx2.IsSupported"/>.</summary>
    internal static int Sad16x16Avx2(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        ref var ra = ref MemoryMarshal.GetReference(a);
        ref var rb = ref MemoryMarshal.GetReference(b);
        return Sad16xNAvx2(ref ra, strideA, ref rb, strideB, height: 16);
    }

    /// <summary>16×8 SAD via AVX2 only (parity tests).</summary>
    internal static int Sad16x8Avx2(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        ref var ra = ref MemoryMarshal.GetReference(a);
        ref var rb = ref MemoryMarshal.GetReference(b);
        return Sad16xNAvx2(ref ra, strideA, ref rb, strideB, height: 8);
    }

    internal static int Sad8x16Avx2(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        ref var ra = ref MemoryMarshal.GetReference(a);
        ref var rb = ref MemoryMarshal.GetReference(b);
        return Sad8xNAvx2(ref ra, strideA, ref rb, strideB, height: 16);
    }

    internal static int Sad8x8Avx2(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        ref var ra = ref MemoryMarshal.GetReference(a);
        ref var rb = ref MemoryMarshal.GetReference(b);
        return Sad8xNAvx2(ref ra, strideA, ref rb, strideB, height: 8);
    }

    /// <summary>16×16 SAD via 128-bit <c>psadbw</c> only (benchmarks; bypasses AVX2 dispatch).</summary>
    internal static int Sad16x16Ssse3(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        ref var ra = ref MemoryMarshal.GetReference(a);
        ref var rb = ref MemoryMarshal.GetReference(b);
        return Sad16xNSsse3(ref ra, strideA, ref rb, strideB, height: 16);
    }

    internal static int Sad16x8Ssse3(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        ref var ra = ref MemoryMarshal.GetReference(a);
        ref var rb = ref MemoryMarshal.GetReference(b);
        return Sad16xNSsse3(ref ra, strideA, ref rb, strideB, height: 8);
    }

    internal static int Sad8x16Ssse3(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        ref var ra = ref MemoryMarshal.GetReference(a);
        ref var rb = ref MemoryMarshal.GetReference(b);
        return Sad8xNSsse3(ref ra, strideA, ref rb, strideB, height: 16);
    }

    internal static int Sad8x8Ssse3(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        ref var ra = ref MemoryMarshal.GetReference(a);
        ref var rb = ref MemoryMarshal.GetReference(b);
        return Sad8xNSsse3(ref ra, strideA, ref rb, strideB, height: 8);
    }

    internal static int Sad16x16AdvSimd(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        ref var ra = ref MemoryMarshal.GetReference(a);
        ref var rb = ref MemoryMarshal.GetReference(b);
        return Sad16xNAdvSimd(ref ra, strideA, ref rb, strideB, height: 16);
    }

    internal static int Sad16x8AdvSimd(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        ref var ra = ref MemoryMarshal.GetReference(a);
        ref var rb = ref MemoryMarshal.GetReference(b);
        return Sad16xNAdvSimd(ref ra, strideA, ref rb, strideB, height: 8);
    }

    internal static int Sad8x16AdvSimd(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        ref var ra = ref MemoryMarshal.GetReference(a);
        ref var rb = ref MemoryMarshal.GetReference(b);
        return Sad8xNAdvSimd(ref ra, strideA, ref rb, strideB, height: 16);
    }

    internal static int Sad8x8AdvSimd(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        ref var ra = ref MemoryMarshal.GetReference(a);
        ref var rb = ref MemoryMarshal.GetReference(b);
        return Sad8xNAdvSimd(ref ra, strideA, ref rb, strideB, height: 8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SadScalarNxM(
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
                s += Math.Abs(a[oa + x] - b[ob + x]);
            }
        }

        return s;
    }

    private static int Sad16xNIntrinsics(
        ReadOnlySpan<byte> a, int strideA,
        ReadOnlySpan<byte> b, int strideB,
        int height)
    {
        ref var ra = ref MemoryMarshal.GetReference(a);
        ref var rb = ref MemoryMarshal.GetReference(b);
        if (Avx2.IsSupported)
        {
            return Sad16xNAvx2(ref ra, strideA, ref rb, strideB, height);
        }

        if (Ssse3.IsSupported)
        {
            return Sad16xNSsse3(ref ra, strideA, ref rb, strideB, height);
        }

        if (AdvSimd.IsSupported)
        {
            return Sad16xNAdvSimd(ref ra, strideA, ref rb, strideB, height);
        }

        return SadScalarNxM(a, strideA, b, strideB, width: 16, height: height);
    }

    private static int Sad16xNAvx2(ref byte ra, int strideA, ref byte rb, int strideB, int height)
    {
        var acc = Vector256<ulong>.Zero;
        var y = 0;
        for (; y + 1 < height; y += 2)
        {
            ref var rowA0 = ref Unsafe.Add(ref ra, y * strideA);
            ref var rowA1 = ref Unsafe.Add(ref ra, (y + 1) * strideA);
            ref var rowB0 = ref Unsafe.Add(ref rb, y * strideB);
            ref var rowB1 = ref Unsafe.Add(ref rb, (y + 1) * strideB);
            var va1280 = Unsafe.ReadUnaligned<Vector128<byte>>(ref rowA0);
            var va1281 = Unsafe.ReadUnaligned<Vector128<byte>>(ref rowA1);
            var vb1280 = Unsafe.ReadUnaligned<Vector128<byte>>(ref rowB0);
            var vb1281 = Unsafe.ReadUnaligned<Vector128<byte>>(ref rowB1);
            var va = Vector256.Create(va1280, va1281);
            var vb = Vector256.Create(vb1280, vb1281);
            acc = Avx2.Add(acc, Avx2.SumAbsoluteDifferences(va, vb).AsUInt64());
        }

        if ((height & 1) != 0)
        {
            ref var rowA = ref Unsafe.Add(ref ra, y * strideA);
            ref var rowB = ref Unsafe.Add(ref rb, y * strideB);
            var va128 = Unsafe.ReadUnaligned<Vector128<byte>>(ref rowA);
            var vb128 = Unsafe.ReadUnaligned<Vector128<byte>>(ref rowB);
            var va = Vector256.Create(va128, default);
            var vb = Vector256.Create(vb128, default);
            acc = Avx2.Add(acc, Avx2.SumAbsoluteDifferences(va, vb).AsUInt64());
        }

        return (int)(acc.GetElement(0) + acc.GetElement(1) + acc.GetElement(2) + acc.GetElement(3));
    }

    private static int Sad16xNSsse3(ref byte ra, int strideA, ref byte rb, int strideB, int height)
    {
        var acc = Vector128<ulong>.Zero;
        for (var y = 0; y < height; y++)
        {
            ref var rowA = ref Unsafe.Add(ref ra, y * strideA);
            ref var rowB = ref Unsafe.Add(ref rb, y * strideB);
            var va = Unsafe.ReadUnaligned<Vector128<byte>>(ref rowA);
            var vb = Unsafe.ReadUnaligned<Vector128<byte>>(ref rowB);
            acc = Sse2.Add(acc, Sse2.SumAbsoluteDifferences(va, vb).AsUInt64());
        }

        return (int)(acc.GetElement(0) + acc.GetElement(1));
    }

    private static int Sad8xNAvx2(ref byte ra, int strideA, ref byte rb, int strideB, int height)
    {
        var acc = Vector256<ulong>.Zero;
        var y = 0;
        for (; y + 1 < height; y += 2)
        {
            ref var rowA0 = ref Unsafe.Add(ref ra, y * strideA);
            ref var rowA1 = ref Unsafe.Add(ref ra, (y + 1) * strideA);
            ref var rowB0 = ref Unsafe.Add(ref rb, y * strideB);
            ref var rowB1 = ref Unsafe.Add(ref rb, (y + 1) * strideB);
            var loA0 = Unsafe.ReadUnaligned<ulong>(ref rowA0);
            var loA1 = Unsafe.ReadUnaligned<ulong>(ref rowA1);
            var loB0 = Unsafe.ReadUnaligned<ulong>(ref rowB0);
            var loB1 = Unsafe.ReadUnaligned<ulong>(ref rowB1);
            var va1280 = Vector128.Create(loA0, 0UL).AsByte();
            var va1281 = Vector128.Create(loA1, 0UL).AsByte();
            var vb1280 = Vector128.Create(loB0, 0UL).AsByte();
            var vb1281 = Vector128.Create(loB1, 0UL).AsByte();
            var va = Vector256.Create(va1280, va1281);
            var vb = Vector256.Create(vb1280, vb1281);
            acc = Avx2.Add(acc, Avx2.SumAbsoluteDifferences(va, vb).AsUInt64());
        }

        if ((height & 1) != 0)
        {
            ref var rowA = ref Unsafe.Add(ref ra, y * strideA);
            ref var rowB = ref Unsafe.Add(ref rb, y * strideB);
            var loA = Unsafe.ReadUnaligned<ulong>(ref rowA);
            var loB = Unsafe.ReadUnaligned<ulong>(ref rowB);
            var va128 = Vector128.Create(loA, 0UL).AsByte();
            var vb128 = Vector128.Create(loB, 0UL).AsByte();
            var va = Vector256.Create(va128, default);
            var vb = Vector256.Create(vb128, default);
            acc = Avx2.Add(acc, Avx2.SumAbsoluteDifferences(va, vb).AsUInt64());
        }

        return (int)(acc.GetElement(0) + acc.GetElement(1) + acc.GetElement(2) + acc.GetElement(3));
    }

    private static int Sad16xNAdvSimd(ref byte ra, int strideA, ref byte rb, int strideB, int height)
    {
        var acc = Vector128<ushort>.Zero;
        for (var y = 0; y < height; y++)
        {
            ref var rowA = ref Unsafe.Add(ref ra, y * strideA);
            ref var rowB = ref Unsafe.Add(ref rb, y * strideB);
            var va = Unsafe.ReadUnaligned<Vector128<byte>>(ref rowA);
            var vb = Unsafe.ReadUnaligned<Vector128<byte>>(ref rowB);
            var diff = AdvSimd.AbsoluteDifference(va, vb);
            acc = Vector128.Add(acc, Vector128.WidenLower(diff));
            acc = Vector128.Add(acc, Vector128.WidenUpper(diff));
        }

        return ReduceUshortAccumulator(acc);
    }

    private static int Sad8xNSsse3(ref byte ra, int strideA, ref byte rb, int strideB, int height)
    {
        var acc = Vector128<ulong>.Zero;
        for (var y = 0; y < height; y++)
        {
            ref var rowA = ref Unsafe.Add(ref ra, y * strideA);
            ref var rowB = ref Unsafe.Add(ref rb, y * strideB);
            var loA = Unsafe.ReadUnaligned<ulong>(ref rowA);
            var loB = Unsafe.ReadUnaligned<ulong>(ref rowB);
            var va = Vector128.Create(loA, 0UL).AsByte();
            var vb = Vector128.Create(loB, 0UL).AsByte();
            acc = Sse2.Add(acc, Sse2.SumAbsoluteDifferences(va, vb).AsUInt64());
        }

        return (int)acc.GetElement(0);
    }

    private static int Sad8xNAdvSimd(ref byte ra, int strideA, ref byte rb, int strideB, int height)
    {
        var acc = Vector128<ushort>.Zero;
        for (var y = 0; y < height; y++)
        {
            ref var rowA = ref Unsafe.Add(ref ra, y * strideA);
            ref var rowB = ref Unsafe.Add(ref rb, y * strideB);
            var va = Unsafe.ReadUnaligned<Vector64<byte>>(ref rowA);
            var vb = Unsafe.ReadUnaligned<Vector64<byte>>(ref rowB);
            var diff = AdvSimd.AbsoluteDifference(va, vb);
            acc = Vector128.Add(acc, Vector128.WidenLower(Vector128.Create(diff, Vector64<byte>.Zero)));
        }

        return ReduceUshortAccumulator(acc);
    }

    private static int Sad8xNIntrinsics(
        ReadOnlySpan<byte> a, int strideA,
        ReadOnlySpan<byte> b, int strideB,
        int height)
    {
        ref var ra = ref MemoryMarshal.GetReference(a);
        ref var rb = ref MemoryMarshal.GetReference(b);
        if (Ssse3.IsSupported)
        {
            return Sad8xNSsse3(ref ra, strideA, ref rb, strideB, height);
        }

        if (AdvSimd.IsSupported)
        {
            return Sad8xNAdvSimd(ref ra, strideA, ref rb, strideB, height);
        }

        return SadScalarNxM(a, strideA, b, strideB, width: 8, height: height);
    }

    /// <summary>
    /// Single horizontal reduction of a Vector128&lt;ushort&gt; SAD accumulator. Uses
    /// <see cref="AdvSimd.Arm64.AddAcrossWidening(Vector128{ushort})"/> on AArch64 — one ADDLV
    /// instruction with widening to uint, eliminating the per-byte scalar epilogue. Falls back to
    /// scalar lane sum on 32-bit Arm where Arm64 intrinsics are unavailable.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReduceUshortAccumulator(Vector128<ushort> acc)
    {
        if (AdvSimd.Arm64.IsSupported)
        {
            return (int)AdvSimd.Arm64.AddAcrossWidening(acc).ToScalar();
        }

        var sum = 0;
        for (var i = 0; i < 8; i++)
        {
            sum += acc.GetElement(i);
        }

        return sum;
    }
}
