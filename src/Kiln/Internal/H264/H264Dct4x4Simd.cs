using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Kiln.Internal.H264;

/// <summary>4×4 integer DCT forward + fixed-point inverse matrix multiply (SSE4.1 / NEON); inverse divides by 1024.</summary>
internal static class H264Dct4x4Simd
{
    public static bool IsSupported => Sse41.IsSupported || AdvSimd.Arm64.IsSupported;

    /// <summary>Matches <see cref="H264BlockTransform.ForwardDct4X4Scalar"/>.</summary>
    public static void ForwardDct4X4(ReadOnlySpan<short> residual4X4, Span<int> outCoeff)
    {
        if (residual4X4.Length < 16 || outCoeff.Length < 16)
        {
            throw new ArgumentException("Spans must hold 16 elements.");
        }

        ref var rBase = ref MemoryMarshal.GetReference(residual4X4);

        var br0 = Butterfly(WidenRow4(ref rBase, 0));
        var br1 = Butterfly(WidenRow4(ref rBase, 4));
        var br2 = Butterfly(WidenRow4(ref rBase, 8));
        var br3 = Butterfly(WidenRow4(ref rBase, 12));

        Transpose4X4Int32(br0, br1, br2, br3, out var col0, out var col1, out var col2, out var col3);
        var sc0 = Butterfly(col0);
        var sc1 = Butterfly(col1);
        var sc2 = Butterfly(col2);
        var sc3 = Butterfly(col3);
        Transpose4X4Int32(sc0, sc1, sc2, sc3, out var out0, out var out1, out var out2, out var out3);

        ref var oBase = ref MemoryMarshal.GetReference(outCoeff);
        out0.StoreUnsafe(ref oBase);
        out1.StoreUnsafe(ref Unsafe.Add(ref oBase, 4));
        out2.StoreUnsafe(ref Unsafe.Add(ref oBase, 8));
        out3.StoreUnsafe(ref Unsafe.Add(ref oBase, 12));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> WidenRow4(ref short rBase, int shortOffset)
    {
        var row64 = Vector64.LoadUnsafe(ref Unsafe.Add(ref rBase, shortOffset));
        if (Sse41.IsSupported)
        {
            var row128 = Vector128.Create(row64, Vector64<short>.Zero);
            return Sse41.ConvertToVector128Int32(row128);
        }

        return AdvSimd.SignExtendWideningLower(row64);
    }

    /// <summary>4×4 int transpose via unpack (SSE <see cref="Sse2.UnpackLow"/> / NEON <see cref="AdvSimd.Arm64.ZipLow"/>).</summary>
    private static void Transpose4X4Int32(
        Vector128<int> r0,
        Vector128<int> r1,
        Vector128<int> r2,
        Vector128<int> r3,
        out Vector128<int> c0,
        out Vector128<int> c1,
        out Vector128<int> c2,
        out Vector128<int> c3)
    {
        var t0 = ZipIntsLow(r0, r1);
        var t2 = ZipIntsHigh(r0, r1);
        var t1 = ZipIntsLow(r2, r3);
        var t3 = ZipIntsHigh(r2, r3);
        var u0 = ZipIntsLow(t0, t1);
        var u1 = ZipIntsHigh(t0, t1);
        var u2 = ZipIntsLow(t2, t3);
        var u3 = ZipIntsHigh(t2, t3);
        var fix = Vector128.Create(0, 2, 1, 3);
        c0 = Vector128.Shuffle(u0, fix);
        c1 = Vector128.Shuffle(u1, fix);
        c2 = Vector128.Shuffle(u2, fix);
        c3 = Vector128.Shuffle(u3, fix);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> ZipIntsLow(Vector128<int> a, Vector128<int> b) =>
        Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> ZipIntsHigh(Vector128<int> a, Vector128<int> b) =>
        Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);

    /// <summary>
    /// 4-point forward butterfly matching H.264 §8.5.8 spec order.
    /// Given o=[x0,x1,x2,x3], produces [s0+s1, 2*d0+d1, s0-s1, d0-2*d1]
    /// where s0=x0+x3, s1=x1+x2, d0=x0-x3, d1=x1-x2.
    /// </summary>
    private static Vector128<int> Butterfly(Vector128<int> o)
    {
        var rev = Reverse4(o);
        var sum = o + rev;
        var diff = o - rev;

        // s0=sum[0], s1=sum[1], d0=diff[0], d1=diff[1]
        // Outputs: b0=s0+s1, b1=2*d0+d1, b2=s0-s1, b3=d0-2*d1
        if (Sse2.IsSupported)
        {
            var s0b = Sse2.Shuffle(sum.AsSingle(), sum.AsSingle(), 0x00).AsInt32();
            var d0b = Sse2.Shuffle(diff.AsSingle(), diff.AsSingle(), 0x00).AsInt32();
            var s1b = Sse2.Shuffle(sum.AsSingle(), sum.AsSingle(), 0x55).AsInt32();
            var d1b = Sse2.Shuffle(diff.AsSingle(), diff.AsSingle(), 0x55).AsInt32();
            var d0s = Sse2.ShiftLeftLogical(d0b, 1);  // 2*d0
            var d1s = Sse2.ShiftLeftLogical(d1b, 1);  // 2*d1
            var b0 = s0b + s1b;
            var b1 = d0s + d1b;   // 2*d0 + d1
            var b2 = s0b - s1b;
            var b3 = d0b - d1s;   // d0 - 2*d1
            var lo = Sse2.UnpackLow(b0, b1);  // [b0, b1, b0, b1]
            var hi = Sse2.UnpackLow(b2, b3);  // [b2, b3, b2, b3]
            return Sse2.Shuffle(lo.AsSingle(), hi.AsSingle(), 0x44).AsInt32();  // [b0, b1, b2, b3]
        }
        else
        {
            var s0b = Vector128.Shuffle(sum, Vector128.Create(0, 0, 0, 0));
            var d0b = Vector128.Shuffle(diff, Vector128.Create(0, 0, 0, 0));
            var s1b = Vector128.Shuffle(sum, Vector128.Create(1, 1, 1, 1));
            var d1b = Vector128.Shuffle(diff, Vector128.Create(1, 1, 1, 1));
            var d0s = d0b << 1;  // 2*d0
            var d1s = d1b << 1;  // 2*d1
            var b0 = s0b + s1b;
            var b1 = d0s + d1b;   // 2*d0 + d1
            var b2 = s0b - s1b;
            var b3 = d0b - d1s;   // d0 - 2*d1
            var lo = AdvSimd.Arm64.ZipLow(b0, b1);  // [b0, b1, b0, b1]
            var hi = AdvSimd.Arm64.ZipLow(b2, b3);  // [b2, b3, b2, b3]
            // Concatenate lower 64 bits of lo with lower 64 bits of hi → [b0, b1, b2, b3]
            return AdvSimd.Arm64.ZipLow(lo.AsInt64(), hi.AsInt64()).AsInt32();
        }
    }

    private static Vector128<int> Reverse4(Vector128<int> o)
    {
        if (Sse2.IsSupported)
        {
            // PSHUFD with 0x1B = (3,2,1,0) is cheaper than TBL on x86, so keep the ISA-specific path here.
            return Sse2.Shuffle(o.AsSingle(), o.AsSingle(), 0x1B).AsInt32();
        }

        return Vector128.Shuffle(o, Vector128.Create(3, 2, 1, 0));
    }

    /// <summary>Matches <see cref="H264BlockTransform.InverseDctMatrixMultiplyScalar"/>.</summary>
    public static void InverseDctMatrixMultiply(ReadOnlySpan<int> matrix, ReadOnlySpan<int> fwdDomain, Span<int> residual16)
    {
        if (matrix.Length < 256 || fwdDomain.Length < 16 || residual16.Length < 16)
        {
            throw new ArgumentException("Invalid span lengths for inverse DCT.");
        }

        ref var mRef = ref MemoryMarshal.GetReference(matrix);
        ref var fRef = ref MemoryMarshal.GetReference(fwdDomain);
        const int scale = 1024;
        for (var row = 0; row < 16; row++)
        {
            var rowAcc = Vector128<int>.Zero;
            var off = row * 16;
            for (var k = 0; k < 16; k += 4)
            {
                var fv = Vector128.LoadUnsafe(ref Unsafe.Add(ref fRef, k));
                ref var mk = ref Unsafe.Add(ref mRef, off + k);
                var mv = Vector128.LoadUnsafe(ref mk);
                // 32-bit lane multiply has different ISA shapes (Sse41.MultiplyLow vs AdvSimd.Multiply); accumulate via portable +.
                var prod = Sse41.IsSupported
                    ? Sse41.MultiplyLow(fv, mv)
                    : AdvSimd.Multiply(fv, mv);
                rowAcc += prod;
            }

            long sum;
            if (Sse41.IsSupported)
            {
                var t = Ssse3.HorizontalAdd(rowAcc, rowAcc);
                var t2 = Ssse3.HorizontalAdd(t, t);
                sum = t2.GetElement(0);
            }
            else
            {
                sum = AdvSimd.Arm64.AddAcross(rowAcc).ToScalar();
            }

            residual16[row] = (int)(sum / scale);
        }
    }
}
