using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Kiln.Internal.H264;

/// <summary>Integer-pel block SATD for inter motion estimation (4×4 Hadamard sum).</summary>
internal static class H264MotionSatd
{
    internal const int Transform4x4CoefficientCount = 16;

    public static bool IsSupported => H264Hadamard4x4.SimdIsSupported;

    public static int Satd16x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
        => SatdNxM(a, strideA, b, strideB, blocksX: 4, blocksY: 4);

    public static int Satd16x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
        => SatdNxM(a, strideA, b, strideB, blocksX: 4, blocksY: 2);

    public static int Satd8x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
        => SatdNxM(a, strideA, b, strideB, blocksX: 2, blocksY: 4);

    public static int Satd8x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
        => SatdNxM(a, strideA, b, strideB, blocksX: 2, blocksY: 2);

    internal static int Satd16x16Scalar(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        SatdNxM(a, strideA, b, strideB, blocksX: 4, blocksY: 4);

    internal static int Satd16x8Scalar(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        SatdNxM(a, strideA, b, strideB, blocksX: 4, blocksY: 2);

    internal static int Satd8x16Scalar(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        SatdNxM(a, strideA, b, strideB, blocksX: 2, blocksY: 4);

    internal static int Satd8x8Scalar(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        SatdNxM(a, strideA, b, strideB, blocksX: 2, blocksY: 2);

    internal static int Satd16x16Simd(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        SatdNxMSimd(a, strideA, b, strideB, blocksX: 4, blocksY: 4);

    internal static int Satd16x8Simd(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        SatdNxMSimd(a, strideA, b, strideB, blocksX: 4, blocksY: 2);

    internal static int Satd8x16Simd(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        SatdNxMSimd(a, strideA, b, strideB, blocksX: 2, blocksY: 4);

    internal static int Satd8x8Simd(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        SatdNxMSimd(a, strideA, b, strideB, blocksX: 2, blocksY: 2);

    internal static void SatdMany4x4Scalar(ReadOnlySpan<byte> src, ReadOnlySpan<byte> predConcat, Span<int> satds, int count)
    {
        for (var i = 0; i < count; i++)
        {
            satds[i] = H264Hadamard4x4.Satd(src, predConcat.Slice(i * 16, 16));
        }
    }

    internal static void SatdMany4x4Simd(ReadOnlySpan<byte> src, ReadOnlySpan<byte> predConcat, Span<int> satds, int count)
    {
        Span<short> residual16 = stackalloc short[16];
        for (var i = 0; i < count; i++)
        {
            Diff4x4ContiguousSimd(src, predConcat.Slice(i * 16, 16), residual16);
            satds[i] = HadamardSatdInt16Simd(residual16);
        }
    }

    private static int SatdNxM(
        ReadOnlySpan<byte> a, int strideA,
        ReadOnlySpan<byte> b, int strideB,
        int blocksX, int blocksY)
    {
        var sum = 0;
        for (var by = 0; by < blocksY; by++)
        {
            for (var bx = 0; bx < blocksX; bx++)
            {
                sum += Satd4x4Strided(a, strideA, bx * 4, by * 4, b, strideB, bx * 4, by * 4);
            }
        }

        return sum;
    }

    private static int SatdNxMSimd(
        ReadOnlySpan<byte> a, int strideA,
        ReadOnlySpan<byte> b, int strideB,
        int blocksX, int blocksY)
    {
        if (!IsSupported)
        {
            return SatdNxM(a, strideA, b, strideB, blocksX, blocksY);
        }

        var sum = 0;
        for (var by = 0; by < blocksY; by++)
        {
            for (var bx = 0; bx < blocksX; bx++)
            {
                sum += Satd4x4StridedSimd(a, strideA, bx * 4, by * 4, b, strideB, bx * 4, by * 4);
            }
        }

        return sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Satd4x4StridedSimd(
        ReadOnlySpan<byte> a, int strideA, int ax, int ay,
        ReadOnlySpan<byte> b, int strideB, int bx, int by)
    {
        Span<short> residual16 = stackalloc short[16];
        Diff4x4StridedSimd(a, strideA, ax, ay, b, strideB, bx, by, residual16);
        return HadamardSatdInt16Simd(residual16);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Diff4x4ContiguousSimd(ReadOnlySpan<byte> src, ReadOnlySpan<byte> pred, Span<short> residual16)
        => Diff4x4StridedSimd(src, strideA: 4, ax: 0, ay: 0, pred, strideB: 4, bx: 0, by: 0, residual16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Diff4x4StridedSimd(
        ReadOnlySpan<byte> a, int strideA, int ax, int ay,
        ReadOnlySpan<byte> b, int strideB, int bx, int by,
        Span<short> residual16)
    {
        if (Sse41.IsSupported)
        {
            for (var row = 0; row < 4; row++)
            {
                var oa = (ay + row) * strideA + ax;
                var ob = (by + row) * strideB + bx;
                var va = Sse41.ConvertToVector128Int32(Vector128.LoadUnsafe(ref Unsafe.AsRef(in a[oa])));
                var vb = Sse41.ConvertToVector128Int32(Vector128.LoadUnsafe(ref Unsafe.AsRef(in b[ob])));
                StoreDiffRowInt16(va - vb, residual16, row * 4);
            }

            return;
        }

        if (AdvSimd.IsSupported)
        {
            for (var row = 0; row < 4; row++)
            {
                var oa = (ay + row) * strideA + ax;
                var ob = (by + row) * strideB + bx;
                for (var col = 0; col < 4; col++)
                {
                    residual16[(row * 4) + col] = (short)(a[oa + col] - b[ob + col]);
                }
            }

            return;
        }

        for (var row = 0; row < 4; row++)
        {
            var oa = (ay + row) * strideA + ax;
            var ob = (by + row) * strideB + bx;
            for (var col = 0; col < 4; col++)
            {
                residual16[(row * 4) + col] = (short)(a[oa + col] - b[ob + col]);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreDiffRowInt16(Vector128<int> diff, Span<short> dst, int offset)
    {
        ref var d = ref MemoryMarshal.GetReference(dst);
        Unsafe.Add(ref d, offset + 0) = (short)diff.GetElement(0);
        Unsafe.Add(ref d, offset + 1) = (short)diff.GetElement(1);
        Unsafe.Add(ref d, offset + 2) = (short)diff.GetElement(2);
        Unsafe.Add(ref d, offset + 3) = (short)diff.GetElement(3);
    }

    /// <summary>4×4 Hadamard on packed int16 residual; bit-identical to scalar <see cref="Satd4x4Strided"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HadamardSatdInt16Simd(ReadOnlySpan<short> residual16)
    {
        if (H264Hadamard4x4.SimdIsSupported)
        {
            return H264Hadamard4x4.SatdSimd(residual16);
        }

        Span<int> r = stackalloc int[16];
        for (var i = 0; i < 16; i++)
        {
            r[i] = residual16[i];
        }

        for (var row = 0; row < 4; row++)
        {
            var o = row * 4;
            RowHadamard(r[o + 0], r[o + 1], r[o + 2], r[o + 3], out r[o + 0], out r[o + 1], out r[o + 2], out r[o + 3]);
        }

        for (var col = 0; col < 4; col++)
        {
            RowHadamard(r[col + 0], r[col + 4], r[col + 8], r[col + 12],
                out var y0, out var y1, out var y2, out var y3);
            r[col + 0] = y0;
            r[col + 4] = y1;
            r[col + 8] = y2;
            r[col + 12] = y3;
        }

        var sum = 0;
        for (var i = 0; i < 16; i++)
        {
            sum += Math.Abs(r[i]);
        }

        return (sum + 1) >> 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Transform4x4Strided(
        ReadOnlySpan<byte> source,
        int stride,
        int x,
        int y,
        Span<short> coefficients)
    {
        var o0 = y * stride + x;
        var o1 = o0 + stride;
        var o2 = o1 + stride;
        var o3 = o2 + stride;

        RowHadamard(
            source[o0], source[o0 + 1], source[o0 + 2], source[o0 + 3],
            out var r00, out var r01, out var r02, out var r03);
        RowHadamard(
            source[o1], source[o1 + 1], source[o1 + 2], source[o1 + 3],
            out var r10, out var r11, out var r12, out var r13);
        RowHadamard(
            source[o2], source[o2 + 1], source[o2 + 2], source[o2 + 3],
            out var r20, out var r21, out var r22, out var r23);
        RowHadamard(
            source[o3], source[o3 + 1], source[o3 + 2], source[o3 + 3],
            out var r30, out var r31, out var r32, out var r33);

        StoreColumnHadamard(r00, r10, r20, r30, coefficients, 0);
        StoreColumnHadamard(r01, r11, r21, r31, coefficients, 4);
        StoreColumnHadamard(r02, r12, r22, r32, coefficients, 8);
        StoreColumnHadamard(r03, r13, r23, r33, coefficients, 12);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Satd4x4FromTransformed(ReadOnlySpan<short> sourceCoefficients, ReadOnlySpan<short> referenceCoefficients)
    {
        var sum = 0;
        for (var i = 0; i < Transform4x4CoefficientCount; i++)
            sum += Math.Abs(sourceCoefficients[i] - referenceCoefficients[i]);
        return (sum + 1) >> 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Satd4x4Strided(
        ReadOnlySpan<byte> a, int strideA, int ax, int ay,
        ReadOnlySpan<byte> b, int strideB, int bx, int by)
    {
        var ao0 = ay * strideA + ax;
        var ao1 = ao0 + strideA;
        var ao2 = ao1 + strideA;
        var ao3 = ao2 + strideA;
        var bo0 = by * strideB + bx;
        var bo1 = bo0 + strideB;
        var bo2 = bo1 + strideB;
        var bo3 = bo2 + strideB;

        RowHadamard(
            a[ao0] - b[bo0], a[ao0 + 1] - b[bo0 + 1],
            a[ao0 + 2] - b[bo0 + 2], a[ao0 + 3] - b[bo0 + 3],
            out var r00, out var r01, out var r02, out var r03);
        RowHadamard(
            a[ao1] - b[bo1], a[ao1 + 1] - b[bo1 + 1],
            a[ao1 + 2] - b[bo1 + 2], a[ao1 + 3] - b[bo1 + 3],
            out var r10, out var r11, out var r12, out var r13);
        RowHadamard(
            a[ao2] - b[bo2], a[ao2 + 1] - b[bo2 + 1],
            a[ao2 + 2] - b[bo2 + 2], a[ao2 + 3] - b[bo2 + 3],
            out var r20, out var r21, out var r22, out var r23);
        RowHadamard(
            a[ao3] - b[bo3], a[ao3 + 1] - b[bo3 + 1],
            a[ao3 + 2] - b[bo3 + 2], a[ao3 + 3] - b[bo3 + 3],
            out var r30, out var r31, out var r32, out var r33);

        var sum = 0;
        sum += ColumnAbsSum(r00, r10, r20, r30);
        sum += ColumnAbsSum(r01, r11, r21, r31);
        sum += ColumnAbsSum(r02, r12, r22, r32);
        sum += ColumnAbsSum(r03, r13, r23, r33);
        return (sum + 1) >> 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreColumnHadamard(
        int x0,
        int x1,
        int x2,
        int x3,
        Span<short> coefficients,
        int offset)
    {
        RowHadamard(x0, x1, x2, x3, out var y0, out var y1, out var y2, out var y3);
        coefficients[offset] = (short)y0;
        coefficients[offset + 1] = (short)y1;
        coefficients[offset + 2] = (short)y2;
        coefficients[offset + 3] = (short)y3;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RowHadamard(
        int x0,
        int x1,
        int x2,
        int x3,
        out int y0,
        out int y1,
        out int y2,
        out int y3)
    {
        var b0 = x0 + x3;
        var b3 = x0 - x3;
        var b1 = x1 + x2;
        var b2 = x1 - x2;
        y0 = b0 + b1;
        y1 = b3 + b2;
        y2 = b0 - b1;
        y3 = b3 - b2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ColumnAbsSum(int x0, int x1, int x2, int x3)
    {
        RowHadamard(x0, x1, x2, x3, out var y0, out var y1, out var y2, out var y3);
        return Math.Abs(y0) + Math.Abs(y1) + Math.Abs(y2) + Math.Abs(y3);
    }
}
