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

    /// <summary>
    /// Scalar reference 16×16 SAD against four reference blocks that share <paramref name="strideB"/>.
    /// Writes one result per candidate to <paramref name="sads"/>.
    /// </summary>
    public static void Sad16x16x4Scalar(
        ReadOnlySpan<byte> a, int strideA,
        ReadOnlySpan<byte> b0, ReadOnlySpan<byte> b1,
        ReadOnlySpan<byte> b2, ReadOnlySpan<byte> b3,
        int strideB, Span<int> sads)
    {
        sads[0] = SadScalarNxM(a, strideA, b0, strideB, width: 16, height: 16);
        sads[1] = SadScalarNxM(a, strideA, b1, strideB, width: 16, height: 16);
        sads[2] = SadScalarNxM(a, strideA, b2, strideB, width: 16, height: 16);
        sads[3] = SadScalarNxM(a, strideA, b3, strideB, width: 16, height: 16);
    }

    /// <summary>
    /// Scalar reference 8×8 SAD against four reference blocks that share <paramref name="strideB"/>.
    /// Writes one result per candidate to <paramref name="sads"/>.
    /// </summary>
    public static void Sad8x8x4Scalar(
        ReadOnlySpan<byte> a, int strideA,
        ReadOnlySpan<byte> b0, ReadOnlySpan<byte> b1,
        ReadOnlySpan<byte> b2, ReadOnlySpan<byte> b3,
        int strideB, Span<int> sads)
    {
        sads[0] = SadScalarNxM(a, strideA, b0, strideB, width: 8, height: 8);
        sads[1] = SadScalarNxM(a, strideA, b1, strideB, width: 8, height: 8);
        sads[2] = SadScalarNxM(a, strideA, b2, strideB, width: 8, height: 8);
        sads[3] = SadScalarNxM(a, strideA, b3, strideB, width: 8, height: 8);
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

    internal static void Sad16x16x4AdvSimd(
        ReadOnlySpan<byte> a, int strideA,
        ReadOnlySpan<byte> b0, ReadOnlySpan<byte> b1,
        ReadOnlySpan<byte> b2, ReadOnlySpan<byte> b3,
        int strideB, Span<int> sads)
    {
        ref var ra = ref MemoryMarshal.GetReference(a);
        ref var rb0 = ref MemoryMarshal.GetReference(b0);
        ref var rb1 = ref MemoryMarshal.GetReference(b1);
        ref var rb2 = ref MemoryMarshal.GetReference(b2);
        ref var rb3 = ref MemoryMarshal.GetReference(b3);

        // Each accumulator lane sums two 8-byte halves per row, so the worst case is
        // 16 rows * 2 * 255 = 8160, which fits in ushort.
        var acc0 = Vector128<ushort>.Zero;
        var acc1 = Vector128<ushort>.Zero;
        var acc2 = Vector128<ushort>.Zero;
        var acc3 = Vector128<ushort>.Zero;
        for (var y = 0; y < 16; y++)
        {
            // One source load per row, reused by all four candidates.
            var va = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref ra, y * strideA));
            var v0 = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref rb0, y * strideB));
            var v1 = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref rb1, y * strideB));
            var v2 = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref rb2, y * strideB));
            var v3 = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref rb3, y * strideB));
            acc0 = AdvSimd.AbsoluteDifferenceWideningLowerAndAdd(acc0, va.GetLower(), v0.GetLower());
            acc0 = AdvSimd.AbsoluteDifferenceWideningUpperAndAdd(acc0, va, v0);
            acc1 = AdvSimd.AbsoluteDifferenceWideningLowerAndAdd(acc1, va.GetLower(), v1.GetLower());
            acc1 = AdvSimd.AbsoluteDifferenceWideningUpperAndAdd(acc1, va, v1);
            acc2 = AdvSimd.AbsoluteDifferenceWideningLowerAndAdd(acc2, va.GetLower(), v2.GetLower());
            acc2 = AdvSimd.AbsoluteDifferenceWideningUpperAndAdd(acc2, va, v2);
            acc3 = AdvSimd.AbsoluteDifferenceWideningLowerAndAdd(acc3, va.GetLower(), v3.GetLower());
            acc3 = AdvSimd.AbsoluteDifferenceWideningUpperAndAdd(acc3, va, v3);
        }

        sads[0] = ReduceUshortAccumulator(acc0);
        sads[1] = ReduceUshortAccumulator(acc1);
        sads[2] = ReduceUshortAccumulator(acc2);
        sads[3] = ReduceUshortAccumulator(acc3);
    }

    internal static void Sad8x8x4AdvSimd(
        ReadOnlySpan<byte> a, int strideA,
        ReadOnlySpan<byte> b0, ReadOnlySpan<byte> b1,
        ReadOnlySpan<byte> b2, ReadOnlySpan<byte> b3,
        int strideB, Span<int> sads)
    {
        ref var ra = ref MemoryMarshal.GetReference(a);
        ref var rb0 = ref MemoryMarshal.GetReference(b0);
        ref var rb1 = ref MemoryMarshal.GetReference(b1);
        ref var rb2 = ref MemoryMarshal.GetReference(b2);
        ref var rb3 = ref MemoryMarshal.GetReference(b3);

        // Each accumulator lane sums one 8-byte half-row per row, so the worst case is
        // 8 rows * 255 = 2040, which fits in ushort.
        var acc0 = Vector128<ushort>.Zero;
        var acc1 = Vector128<ushort>.Zero;
        var acc2 = Vector128<ushort>.Zero;
        var acc3 = Vector128<ushort>.Zero;
        for (var y = 0; y < 8; y++)
        {
            // One source load per row, reused by all four candidates.
            var va = Unsafe.ReadUnaligned<Vector64<byte>>(ref Unsafe.Add(ref ra, y * strideA));
            var v0 = Unsafe.ReadUnaligned<Vector64<byte>>(ref Unsafe.Add(ref rb0, y * strideB));
            var v1 = Unsafe.ReadUnaligned<Vector64<byte>>(ref Unsafe.Add(ref rb1, y * strideB));
            var v2 = Unsafe.ReadUnaligned<Vector64<byte>>(ref Unsafe.Add(ref rb2, y * strideB));
            var v3 = Unsafe.ReadUnaligned<Vector64<byte>>(ref Unsafe.Add(ref rb3, y * strideB));
            acc0 = AdvSimd.AbsoluteDifferenceWideningLowerAndAdd(acc0, va, v0);
            acc1 = AdvSimd.AbsoluteDifferenceWideningLowerAndAdd(acc1, va, v1);
            acc2 = AdvSimd.AbsoluteDifferenceWideningLowerAndAdd(acc2, va, v2);
            acc3 = AdvSimd.AbsoluteDifferenceWideningLowerAndAdd(acc3, va, v3);
        }

        sads[0] = ReduceUshortAccumulator(acc0);
        sads[1] = ReduceUshortAccumulator(acc1);
        sads[2] = ReduceUshortAccumulator(acc2);
        sads[3] = ReduceUshortAccumulator(acc3);
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
        // UABAL/UABAL2 widen each 8-byte half straight into the ushort accumulator, so one row
        // costs two instructions instead of a byte difference, two widens and two adds. Each lane
        // sums one sample per half-row, so the worst case is height * 2 * 255 <= 16 * 510 = 8160,
        // which fits in ushort.
        var acc = Vector128<ushort>.Zero;
        for (var y = 0; y < height; y++)
        {
            ref var rowA = ref Unsafe.Add(ref ra, y * strideA);
            ref var rowB = ref Unsafe.Add(ref rb, y * strideB);
            var va = Unsafe.ReadUnaligned<Vector128<byte>>(ref rowA);
            var vb = Unsafe.ReadUnaligned<Vector128<byte>>(ref rowB);
            acc = AdvSimd.AbsoluteDifferenceWideningLowerAndAdd(acc, va.GetLower(), vb.GetLower());
            acc = AdvSimd.AbsoluteDifferenceWideningUpperAndAdd(acc, va, vb);
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
        // 64-bit UABAL accumulates one 8-byte row per instruction without padding it to 128 bits.
        // Each lane sums one sample per row, so the worst case is height * 255 <= 16 * 255 = 4080,
        // which fits in ushort.
        var acc = Vector128<ushort>.Zero;
        for (var y = 0; y < height; y++)
        {
            ref var rowA = ref Unsafe.Add(ref ra, y * strideA);
            ref var rowB = ref Unsafe.Add(ref rb, y * strideB);
            var va = Unsafe.ReadUnaligned<Vector64<byte>>(ref rowA);
            var vb = Unsafe.ReadUnaligned<Vector64<byte>>(ref rowB);
            acc = AdvSimd.AbsoluteDifferenceWideningLowerAndAdd(acc, va, vb);
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
