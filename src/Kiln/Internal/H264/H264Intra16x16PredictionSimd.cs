using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Kiln.Internal.H264;

/// <summary>
/// SIMD Intra_16×16 luma prediction kernels (modes 0–3) and 16×16 SAD. All outputs are bit-exact
/// to the scalar implementations in <see cref="H264Intra16x16Prediction"/>. Requires
/// <see cref="Vector128.IsHardwareAccelerated"/>; callers must gate on that before calling.
/// </summary>
internal static class H264Intra16x16PredictionSimd
{
    public static bool IsSupported => Sse2.IsSupported || AdvSimd.Arm64.IsSupported;

    /// <summary>
    /// Vertical prediction (mode 0): load the 16-byte top row once, store it to all 16 rows.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PredictVertical(Span<byte> dst, ReadOnlySpan<byte> top)
    {
        ref var rdst = ref MemoryMarshal.GetReference(dst);
        var row = Unsafe.ReadUnaligned<Vector128<byte>>(ref MemoryMarshal.GetReference(top));
        for (var y = 0; y < 16; y++)
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref rdst, y * 16), row);
        }
    }

    /// <summary>
    /// Horizontal prediction (mode 1): broadcast left[y] to fill each row y.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PredictHorizontal(Span<byte> dst, ReadOnlySpan<byte> left)
    {
        ref var rdst = ref MemoryMarshal.GetReference(dst);
        for (var y = 0; y < 16; y++)
        {
            var row = Vector128.Create(left[y]);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref rdst, y * 16), row);
        }
    }

    /// <summary>
    /// DC prediction (mode 2): sum 16 top and/or 16 left samples via horizontal SIMD reduce,
    /// compute the DC value, then broadcast it across all 256 output bytes.
    /// </summary>
    public static void PredictDC(
        Span<byte> dst,
        ReadOnlySpan<byte> top, bool topAvail,
        ReadOnlySpan<byte> left, bool leftAvail)
    {
        int dc;
        if (topAvail && leftAvail)
        {
            dc = (HorizontalSum16(top) + HorizontalSum16(left) + 16) >> 5;
        }
        else if (topAvail)
        {
            dc = (HorizontalSum16(top) + 8) >> 4;
        }
        else if (leftAvail)
        {
            dc = (HorizontalSum16(left) + 8) >> 4;
        }
        else
        {
            dc = 128;
        }

        var fill = Vector128.Create((byte)(dc & 0xFF)); // dc is already in [0,255] by construction
        ref var rdst = ref MemoryMarshal.GetReference(dst);
        for (var y = 0; y < 16; y++)
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref rdst, y * 16), fill);
        }
    }

    /// <summary>
    /// Plane prediction (mode 3): compute scalar H/V sums and coefficients a, b, c per spec
    /// 8.3.3.4, then vectorize the inner fill per row.
    /// Per-column offsets are precomputed as four <see cref="Vector128{T}"/> int32 registers (4 lanes ×
    /// 4 = 16 columns). Per row: add the row constant, shift right by 5, narrow int32→int16
    /// (truncating; safe since result is in ~[−570,570]), clamp to [0,255], narrow uint16→uint8,
    /// and store 16 bytes. All narrows use the generic <see cref="Vector128.Narrow"/> API so the
    /// JIT emits PACKSSDW/PACKUSWB on x86 and SQXTN/UZP1 on NEON.
    /// </summary>
    public static void PredictPlane(
        Span<byte> dst,
        ReadOnlySpan<byte> top,
        ReadOnlySpan<byte> left,
        byte topLeft)
    {
        // Build p[] per H.264 8.3.3.4.
        Span<int> p = stackalloc int[33];
        p[0] = topLeft;
        for (var i = 0; i < 16; i++) p[1 + i] = top[i];
        for (var i = 0; i < 16; i++) p[17 + i] = left[i];

        var hSum = 0;
        for (var i = 0; i < 8; i++) hSum += (i + 1) * (p[1 + 8 + i] - p[1 + 6 - i]);
        var vSum = 0;
        // §8.3.3.4: V uses p[−1, 6−y']; for y'=7 that is p[−1,−1] = topLeft (p[0]), not p[16]
        // (which is topRow[15]). The left column lives at p[17..32], so 6−j only stays in range
        // for j≤6. Must match the scalar path in H264Intra16x16Prediction.
        for (var j = 0; j < 8; j++) vSum += (j + 1) * (p[17 + 8 + j] - (j < 7 ? p[17 + 6 - j] : p[0]));

        var b = (5 * hSum + 32) >> 6;
        var c = (5 * vSum + 32) >> 6;
        var a = 16 * (p[17 + 15] + p[1 + 15]);

        // Precompute per-column addends as four Vector128<int> (4 lanes × 4 = 16 columns).
        // Values can reach ~±10000, so int32 is required (int16 would overflow).
        var xOff0 = Vector128.Create(b * (0 - 7), b * (1 - 7), b * (2 - 7), b * (3 - 7));
        var xOff1 = Vector128.Create(b * (4 - 7), b * (5 - 7), b * (6 - 7), b * (7 - 7));
        var xOff2 = Vector128.Create(b * (8 - 7), b * (9 - 7), b * (10 - 7), b * (11 - 7));
        var xOff3 = Vector128.Create(b * (12 - 7), b * (13 - 7), b * (14 - 7), b * (15 - 7));

        var zero16 = Vector128<short>.Zero;
        var cap16 = Vector128.Create((short)255);

        ref var rdst = ref MemoryMarshal.GetReference(dst);
        for (var y = 0; y < 16; y++)
        {
            var rowBase = a + c * (y - 7) + 16;
            var rb = Vector128.Create(rowBase);

            var v0 = Vector128.ShiftRightArithmetic(Vector128.Add(rb, xOff0), 5);
            var v1 = Vector128.ShiftRightArithmetic(Vector128.Add(rb, xOff1), 5);
            var v2 = Vector128.ShiftRightArithmetic(Vector128.Add(rb, xOff2), 5);
            var v3 = Vector128.ShiftRightArithmetic(Vector128.Add(rb, xOff3), 5);

            // int32x4 → int16x8 (truncating narrow; safe since post-shift values are in ~[−570,570])
            var s01 = Vector128.Narrow(v0, v1);
            var s23 = Vector128.Narrow(v2, v3);

            // Clamp to [0, 255] — maps to PMAXSW/PMINSW on x86, SMAX/SMIN on NEON.
            s01 = Vector128.Max(zero16, Vector128.Min(cap16, s01));
            s23 = Vector128.Max(zero16, Vector128.Min(cap16, s23));

            // uint16x8 → uint8x16 (truncating narrow; safe since values are in [0,255]).
            // On x86 the JIT lowers Narrow(ushort,ushort)→byte to PACKUSWB (unsigned saturate),
            // which is equivalent here since we already clamped.
            var bytes = Vector128.Narrow(s01.AsUInt16(), s23.AsUInt16());
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref rdst, y * 16), bytes);
        }
    }

    /// <summary>
    /// 16×16 luma SAD: sum |src[y*srcStride + x] − pred[y*16 + x]| for y∈[0,15], x∈[0,15].
    /// Accumulates 16 rows of 16-byte <see cref="Vector128{T}"/> absolute-difference reductions.
    /// Pattern mirrors <see cref="H264ChromaSadSimd"/> extended to 16 rows of 16 bytes.
    /// </summary>
    public static int Sad16x16(ReadOnlySpan<byte> src, ReadOnlySpan<byte> pred, int srcStride)
    {
        if (Sse2.IsSupported) return Sad16x16Sse2(src, pred, srcStride);
        if (AdvSimd.IsSupported) return Sad16x16Neon(src, pred, srcStride);
        return Sad16x16Scalar(src, pred, srcStride);
    }

    internal static int Sad16x16Ssse2(ReadOnlySpan<byte> src, ReadOnlySpan<byte> pred, int srcStride) =>
        Sad16x16Sse2(src, pred, srcStride);

    internal static int Sad16x16Neon64(ReadOnlySpan<byte> src, ReadOnlySpan<byte> pred, int srcStride) =>
        Sad16x16NeonArm64(src, pred, srcStride);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Sad16x16Sse2(ReadOnlySpan<byte> src, ReadOnlySpan<byte> pred, int srcStride)
    {
        ref var rsrc = ref MemoryMarshal.GetReference(src);
        ref var rpred = ref MemoryMarshal.GetReference(pred);

        // PSADBW produces two ushort partials per row (lanes 0 and 4).
        // Each partial ≤ 8×255=2040; 16 rows × 2040 = 32640 ≤ ushort max.
        var acc = Vector128<ushort>.Zero;
        for (var y = 0; y < 16; y++)
        {
            var s = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref rsrc, y * srcStride));
            var p = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref rpred, y * 16));
            acc = Sse2.Add(acc, Sse2.SumAbsoluteDifferences(s, p));
        }

        return (int)(acc.GetElement(0) + acc.GetElement(4));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Sad16x16Neon(ReadOnlySpan<byte> src, ReadOnlySpan<byte> pred, int srcStride) =>
        AdvSimd.Arm64.IsSupported
            ? Sad16x16NeonArm64(src, pred, srcStride)
            : Sad16x16NeonScalarReduce(src, pred, srcStride);

    private static int Sad16x16NeonArm64(ReadOnlySpan<byte> src, ReadOnlySpan<byte> pred, int srcStride)
    {
        ref var rsrc = ref MemoryMarshal.GetReference(src);
        ref var rpred = ref MemoryMarshal.GetReference(pred);

        var acc = Vector128<ushort>.Zero;
        for (var y = 0; y < 16; y++)
        {
            var s = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref rsrc, y * srcStride));
            var p = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref rpred, y * 16));
            var diff = AdvSimd.AbsoluteDifference(s, p);
            acc = Vector128.Add(acc, Vector128.WidenLower(diff));
            acc = Vector128.Add(acc, Vector128.WidenUpper(diff));
        }

        return AdvSimd.Arm64.AddAcross(acc).ToScalar();
    }

    private static int Sad16x16NeonScalarReduce(ReadOnlySpan<byte> src, ReadOnlySpan<byte> pred, int srcStride)
    {
        ref var rsrc = ref MemoryMarshal.GetReference(src);
        ref var rpred = ref MemoryMarshal.GetReference(pred);

        var acc = Vector128<ushort>.Zero;
        for (var y = 0; y < 16; y++)
        {
            var s = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref rsrc, y * srcStride));
            var p = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref rpred, y * 16));
            var diff = AdvSimd.AbsoluteDifference(s, p);
            acc = Vector128.Add(acc, Vector128.WidenLower(diff));
            acc = Vector128.Add(acc, Vector128.WidenUpper(diff));
        }

        var sum = 0;
        for (var i = 0; i < 8; i++) sum += acc.GetElement(i);
        return sum;
    }

    private static int Sad16x16Scalar(ReadOnlySpan<byte> src, ReadOnlySpan<byte> pred, int srcStride)
    {
        var sad = 0;
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                sad += Math.Abs(src[y * srcStride + x] - pred[y * 16 + x]);
            }
        }

        return sad;
    }

    /// <summary>
    /// Horizontal sum of 16 bytes. Uses PSADBW-against-zero on x86, UADDLV on AArch64,
    /// or a scalar reduction otherwise.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HorizontalSum16(ReadOnlySpan<byte> span)
    {
        ref var r = ref MemoryMarshal.GetReference(span);
        if (Sse2.IsSupported)
        {
            var v = Unsafe.ReadUnaligned<Vector128<byte>>(ref r);
            var sad = Sse2.SumAbsoluteDifferences(v, Vector128<byte>.Zero).AsUInt64();
            return (int)(sad.GetElement(0) + sad.GetElement(1));
        }

        if (AdvSimd.Arm64.IsSupported)
        {
            var v = Unsafe.ReadUnaligned<Vector128<byte>>(ref r);
            return AdvSimd.Arm64.AddAcrossWidening(v).ToScalar();
        }

        var s = 0;
        for (var i = 0; i < 16; i++) s += Unsafe.Add(ref r, i);
        return s;
    }
}
