using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Kiln.Internal.H264;

/// <summary>SIMD helpers for 4×4 quant (SSE4.1 / NEON).</summary>
internal static class H264BlockTransformSimd
{
    public static bool IsSupported => Sse41.IsSupported || AdvSimd.IsSupported;

    /// <summary>Per-lane MF for one horizontal raster row (coeff indices <c>row·4+k</c>), row-major spectra.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<int> QuantMfPackedForRasterRowQuad(int qpRem6, int row0To3)
    {
        var b = row0To3 << 2;
        return Vector128.Create(
            H264BlockTransform.FullMfForRasterIndex(qpRem6, b),
            H264BlockTransform.FullMfForRasterIndex(qpRem6, b + 1),
            H264BlockTransform.FullMfForRasterIndex(qpRem6, b + 2),
            H264BlockTransform.FullMfForRasterIndex(qpRem6, b + 3));
    }

    /// <summary>Per-lane MF for one spectral column (<c>c + 4·r</c>, <c>r∈{0..3}</c>) matching forward column butterflies.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<int> QuantMfPackedForSpectralColumnQuad(int qpRem6, int spectralCol0To3) =>
        Vector128.Create(
            H264BlockTransform.FullMfForRasterIndex(qpRem6, spectralCol0To3 + (0 << 2)),
            H264BlockTransform.FullMfForRasterIndex(qpRem6, spectralCol0To3 + (1 << 2)),
            H264BlockTransform.FullMfForRasterIndex(qpRem6, spectralCol0To3 + (2 << 2)),
            H264BlockTransform.FullMfForRasterIndex(qpRem6, spectralCol0To3 + (3 << 2)));

    /// <summary>Matches <see cref="H264BlockTransform.Quant4X4"/> per coefficient.</summary>
    public static void Quant4X4(Span<int> block, int qp)
    {
        qp = Math.Clamp(qp, 0, 51);
        var qbits = 15 + (qp / 6);
        var add = 1 << (qbits - 1);
        var qpRem = qp % 6;
        if (Sse41.IsSupported)
        {
            Quant4X4Sse41(block, qpRem, add, qbits);
            return;
        }

        if (AdvSimd.IsSupported)
        {
            Quant4X4AdvSimd(block, qpRem, add, qbits);
            return;
        }

        throw new InvalidOperationException("H264BlockTransformSimd.Quant4X4 requires SSE4.1 or AdvSimd.Arm64.");
    }

    internal static void Quant4X4Sse41(Span<int> block, int qpRem6, int add, int qbits)
    {
        ref var blockRef = ref MemoryMarshal.GetReference(block);
        var addV = Vector128.Create(add);
        var shift = Vector128.CreateScalar(qbits);
        for (var row = 0; row < 4; row++)
        {
            var mfV = QuantMfPackedForRasterRowQuad(qpRem6, row);
            var rowBase = row * 4;
            var v = Vector128.LoadUnsafe(ref Unsafe.Add(ref blockRef, rowBase));
            var mask = Sse2.ShiftRightArithmetic(v, 31);
            var ax = Sse2.Subtract(Sse2.Xor(v, mask), mask);
            var t = Sse2.Add(Sse41.MultiplyLow(ax, mfV), addV);
            var qv = Sse2.ShiftRightArithmetic(t, shift);
            var xs = Sse2.Subtract(Sse2.Xor(qv, mask), mask);
            xs.StoreUnsafe(ref Unsafe.Add(ref blockRef, rowBase));
        }
    }

    internal static void Quant4X4AdvSimd(Span<int> block, int qpRem6, int add, int qbits)
    {
        ref var blockRef = ref MemoryMarshal.GetReference(block);
        var addV = Vector128.Create(add);
        for (var row = 0; row < 4; row++)
        {
            var mfV = QuantMfPackedForRasterRowQuad(qpRem6, row);
            var rowBase = row * 4;
            var v = Vector128.LoadUnsafe(ref Unsafe.Add(ref blockRef, rowBase));
            var mask = AdvSimd.ShiftRightArithmetic(v, 31);
            var ax = AdvSimd.Subtract(AdvSimd.Xor(v, mask), mask);
            var t = AdvSimd.Add(AdvSimd.Multiply(ax, mfV), addV);
#pragma warning disable CA1857 // shift amount from QP; AdvSimd API requires byte
            var qv = AdvSimd.ShiftRightArithmetic(t, (byte)qbits);
#pragma warning restore CA1857
            var xs = AdvSimd.Subtract(AdvSimd.Xor(qv, mask), mask);
            xs.StoreUnsafe(ref Unsafe.Add(ref blockRef, rowBase));
        }
    }
}
