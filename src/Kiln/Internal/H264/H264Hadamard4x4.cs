using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Kiln.Internal.H264;

/// <summary>
/// 4×4 Hadamard SATD (Sum of Absolute Transformed Differences) for intra mode RDO.
/// Used in two-stage I4×4 mode decision: run cheap SAD over all valid modes, take top-K
/// by SAD, then run SATD over those K candidates and pick winner by J = SATD + λ·R.
/// SATD is more correlated with final coded bits than SAD, especially at low QP.
/// </summary>
/// <remarks>
/// The butterfly is the standard Walsh-Hadamard 4×4 (same matrix as H264LumaDcHadamard
/// but operating on the src−pred residual short values rather than integer DC coefficients):
/// <code>
///   H = [[1,1,1,1],[1,1,-1,-1],[1,-1,-1,1],[1,-1,1,-1]]
///   SATD = (Σ|H·r·Hᵀ|) >> 1
/// </code>
/// The >>1 is a scale normalisation that follows from the matrix itself: H·Hᵀ = 4·I, so H is exactly
/// twice an orthonormal matrix and the 2-D composition H·r·Hᵀ is 4× the orthonormal (energy-preserving)
/// transform of r. Halving leaves the coefficients at 2× the orthonormal scale. Because an orthonormal
/// transform preserves the coefficient variance, the L1 sum of the orthonormal coefficients of a
/// decorrelated residual is close to the L1 sum of the residual itself, so the value returned here lands
/// at roughly 2× the SAD of the same block — near enough that the SAD and SATD stages of the mode
/// decision can share one λ scale. The +1 before the shift rounds to nearest.
/// </remarks>
internal static class H264Hadamard4x4
{
    /// <summary>SSE2 or NEON sufficient for widening, butterflies, transpose, and reductions used here.</summary>
    public static bool SimdIsSupported => Sse2.IsSupported || AdvSimd.Arm64.IsSupported;

    /// <summary>
    /// Compute 4×4 SATD between a source block and its prediction.
    /// Both spans must be exactly 16 bytes (4×4 block in raster order).
    /// Returns (sum of absolute Hadamard-transformed residual coefficients) >> 1.
    /// </summary>
    public static int Satd(ReadOnlySpan<byte> src16, ReadOnlySpan<byte> pred16)
    {
        // Residual: src − pred, widened to int to avoid overflow during transform.
        Span<int> r = stackalloc int[16];
        for (var i = 0; i < 16; i++)
            r[i] = src16[i] - pred16[i];

        // Row pass: 4× butterfly (one per row).
        for (var row = 0; row < 4; row++)
        {
            var o = row * 4;
            var x0 = r[o + 0];
            var x1 = r[o + 1];
            var x2 = r[o + 2];
            var x3 = r[o + 3];
            // Outer: b0=x0+x3, b3=x0-x3; inner: b1=x1+x2, b2=x1-x2
            var b0 = x0 + x3;
            var b3 = x0 - x3;
            var b1 = x1 + x2;
            var b2 = x1 - x2;
            r[o + 0] = b0 + b1; // H[0] row: x0+x1+x2+x3
            r[o + 1] = b3 + b2; // H[2] row: x0-x1-x2+x3
            r[o + 2] = b0 - b1; // H[1] row: x0+x1-x2-x3 (permuted but same |coeff| sum)
            r[o + 3] = b3 - b2; // H[3] row: x0-x1+x2-x3
        }

        // Column pass: 4× butterfly (one per column).
        for (var col = 0; col < 4; col++)
        {
            var x0 = r[col + 0];
            var x1 = r[col + 4];
            var x2 = r[col + 8];
            var x3 = r[col + 12];
            var b0 = x0 + x3;
            var b3 = x0 - x3;
            var b1 = x1 + x2;
            var b2 = x1 - x2;
            r[col + 0]  = b0 + b1;
            r[col + 4]  = b3 + b2;
            r[col + 8]  = b0 - b1;
            r[col + 12] = b3 - b2;
        }

        var sum = 0;
        for (var i = 0; i < 16; i++)
            sum += Math.Abs(r[i]);
        // Normalise: H·Hᵀ = 4·I, so H·r·Hᵀ is 4× the orthonormal transform; halving leaves 2× (see the
        // class remarks for why that puts SATD on roughly the same scale as 2× SAD). +1 rounds to nearest.
        return (sum + 1) >> 1;
    }

    /// <summary>
    /// SIMD 4×4 Hadamard SATD on a short residual block (raster 16 samples).
    /// Bit-identical to widening each sample to <see cref="int"/> and running the same transforms as
    /// <see cref="Satd(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/> (plain add/sub, no saturating 16-bit paths).
    /// </summary>
    public static int SatdSimd(ReadOnlySpan<short> residual)
    {
        if (residual.Length < 16)
        {
            throw new ArgumentException("Span must hold 16 elements.");
        }

        ref var rBase = ref MemoryMarshal.GetReference(residual);

        var br0 = HadamardButterfly4(WidenRow4(ref rBase, 0));
        var br1 = HadamardButterfly4(WidenRow4(ref rBase, 4));
        var br2 = HadamardButterfly4(WidenRow4(ref rBase, 8));
        var br3 = HadamardButterfly4(WidenRow4(ref rBase, 12));

        Transpose4X4Int32(br0, br1, br2, br3, out var c0, out var c1, out var c2, out var c3);

        var bc0 = HadamardButterfly4(c0);
        var bc1 = HadamardButterfly4(c1);
        var bc2 = HadamardButterfly4(c2);
        var bc3 = HadamardButterfly4(c3);

        var sumAbs = SumAbs4(bc0) + SumAbs4(bc1) + SumAbs4(bc2) + SumAbs4(bc3);
        return (sumAbs + 1) >> 1;
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

        if (Sse2.IsSupported)
        {
            var vshort = Vector128.Create(row64, Vector64<short>.Zero);
            var sign = Sse2.CompareLessThan(vshort, Vector128<short>.Zero);
            return Sse2.UnpackLow(vshort, sign).AsInt32();
        }

        return AdvSimd.SignExtendWideningLower(row64);
    }

    /// <summary>Same 4-point outer/inner butterfly as the scalar int row/column pass.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> HadamardButterfly4(Vector128<int> o)
    {
        // o = [x0,x1,x2,x3]; b0=x0+x3, b3=x0-x3, b1=x1+x2, b2=x1-x2
        if (Sse2.IsSupported)
        {
            var x0 = Sse2.Shuffle(o.AsSingle(), o.AsSingle(), 0x00).AsInt32();
            var x1 = Sse2.Shuffle(o.AsSingle(), o.AsSingle(), 0x55).AsInt32();
            var x2 = Sse2.Shuffle(o.AsSingle(), o.AsSingle(), 0xAA).AsInt32();
            var x3 = Sse2.Shuffle(o.AsSingle(), o.AsSingle(), 0xFF).AsInt32();
            var b0 = x0 + x3;
            var b3 = x0 - x3;
            var b1 = x1 + x2;
            var b2 = x1 - x2;
            var o0 = b0 + b1;
            var o1 = b3 + b2;
            var o2 = b0 - b1;
            var o3 = b3 - b2;
            var lo = Sse2.UnpackLow(o0, o1);
            var hi = Sse2.UnpackLow(o2, o3);
            return Sse2.Shuffle(lo.AsSingle(), hi.AsSingle(), 0x44).AsInt32();
        }

        var bx0 = Vector128.Shuffle(o, Vector128.Create(0, 0, 0, 0));
        var bx1 = Vector128.Shuffle(o, Vector128.Create(1, 1, 1, 1));
        var bx2 = Vector128.Shuffle(o, Vector128.Create(2, 2, 2, 2));
        var bx3 = Vector128.Shuffle(o, Vector128.Create(3, 3, 3, 3));
        var nb0 = bx0 + bx3;
        var nb3 = bx0 - bx3;
        var nb1 = bx1 + bx2;
        var nb2 = bx1 - bx2;
        var p0 = nb0 + nb1;
        var p1 = nb3 + nb2;
        var p2 = nb0 - nb1;
        var p3 = nb3 - nb2;
        var zlo = AdvSimd.Arm64.ZipLow(p0, p1);
        var zhi = AdvSimd.Arm64.ZipLow(p2, p3);
        return AdvSimd.Arm64.ZipLow(zlo.AsInt64(), zhi.AsInt64()).AsInt32();
    }

    /// <summary>4×4 int transpose: same unpack + lane fix as <see cref="H264Dct4x4Simd"/>.</summary>
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> AbsInt32(Vector128<int> v)
    {
        if (Sse2.IsSupported)
        {
            var sign = Sse2.ShiftRightArithmetic(v, 31);
            return Sse2.Subtract(Sse2.Xor(v, sign), sign);
        }

        return AdvSimd.Abs(v).AsInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SumLanesInt32(Vector128<int> v)
    {
        if (Ssse3.IsSupported)
        {
            var t = Ssse3.HorizontalAdd(v, v);
            var t2 = Ssse3.HorizontalAdd(t, t);
            return t2.GetElement(0);
        }

        if (AdvSimd.Arm64.IsSupported)
        {
            return AdvSimd.Arm64.AddAcross(v).ToScalar();
        }

        return v.GetElement(0) + v.GetElement(1) + v.GetElement(2) + v.GetElement(3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SumAbs4(Vector128<int> v) => SumLanesInt32(AbsInt32(v));
}
