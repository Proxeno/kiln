using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Kiln.Internal.H264;

/// <summary>
/// Fused/bundled 4×4 luma residual encode + reconstruct pipeline. Replaces the per-kernel chain in
/// <see cref="H264BaselineSliceEncoder.WriteMacroblock"/>:
/// <code>
///   residual = src - pred
///   coeff    = ForwardDct(residual)
///   qcoeff   = Quant(coeff, qp)              (in-place over coeff)
///   nz       = popcount(qcoeff != 0)
///   zigzag   = scan(qcoeff)                  (emitted as short for CAVLC)
///   dequant  = Dequant(qcoeff, qp)
///   invRes   = InverseDct(dequant)
///   recon    = clip(pred + invRes)           (written into recDst, strided)
/// </code>
/// </summary>
/// <remarks>
/// <para>The scalar bundle (<see cref="EncodeResidual4x4Scalar"/>) uses separate stack temporaries for
/// coeff / dequant / inverse. The SIMD bundle (<see cref="EncodeResidual4x4Simd"/>) fuses the integer
/// pipeline: quantized spectrum lives as four <see cref="Vector128{T}"/> column vectors from column-DCT
/// through quant; zigzag + nz read those registers; NEON uses <see cref="AdvSimd.Arm64.ZipLow"/> /
/// <see cref="AdvSimd.DuplicateSelectedScalarToVector128"/> for 4×4 transpose and butterfly broadcasts where
/// profitable. Residual rows widen straight from packed UInt32 byte quartets (no intermediate short spill).
/// Zigzag scan matches <see cref="H264Zigzag.Frame4X4"/> with fixed raster lanes (no per-coefficient branch).
/// Luma reconstruction uses §8.5.10/12.2 <see cref="H264BlockTransform.DequantAc4x4Spec"/> +
/// <see cref="H264BlockTransform.InverseDct4x4Spec"/> (scalar after SIMD quant, raster spill from SIMD registers).
/// Chroma retains <see cref="H264BlockTransform.DequantApprox"/> + matrix IDCT coupling.</para>
/// <para><see cref="PreferSimdBundleByDefault"/> defaults to <c>true</c> when the ISA supports <see cref="EncodeResidual4x4Simd"/> (<see cref="IsSimdBundleSupported"/>): fused SIMD is bit-exact with the scalar bundle (<see cref="H264TransformBundleSimdTests"/>) and faster on typical AArch64/x64 configs; benchmark locally if you need to disable it.</para>
/// </remarks>
internal static class H264TransformBundle
{
    /// <summary>ISA coverage for <see cref="EncodeResidual4x4Simd"/>.</summary>
    internal static bool IsSimdBundleSupported =>
        H264Dct4x4Simd.IsSupported && H264BlockTransformSimd.IsSupported && H264BlockTransformDequantSimd.IsSupported;

    /// <summary>
    /// When <see cref="H264IntrinsicsPreference.PreferIntrinsics"/> is true and <see cref="IsSimdBundleSupported"/>, selects SIMD vs scalar fused bundle.
    /// Default <c>true</c> (parity-covered). Set <c>false</c> to force the scalar fused bundle on SIMD-capable hosts.
    /// </summary>
    internal static bool PreferSimdBundleByDefault { get; set; } = true;

    // PSHUFB masks for WriteZigzagCoeffNnUnrolled (raster columns qc0..qc3 packed → H.264 zigzag short order).
    private static readonly Vector128<byte> ZigzagShuffleA = Vector128.Create(
        (byte)0, 1, 8, 9, 2, 3, 4, 5, 10, 11, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);

    private static readonly Vector128<byte> ZigzagShuffleB = Vector128.Create(
        (byte)0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0, 1, 8, 9, 2, 3);

    private static readonly Vector128<byte> ZigzagShuffleC = Vector128.Create(
        (byte)12, 13, 6, 7, 14, 15, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);

    private static readonly Vector128<byte> ZigzagShuffleD = Vector128.Create(
        (byte)0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 4, 5, 10, 11, 12, 13, 6, 7, 14, 15);

    /// <summary>
    /// Bundled scalar residual pipeline. Returns the post-quant non-zero count (for cbp / nnz tables).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int EncodeResidual4x4Scalar(
        ReadOnlySpan<byte> src16,
        ReadOnlySpan<byte> pred16,
        int qp,
        Span<short> zigzagCoeffOut16,
        Span<byte> recDst,
        int recStride)
    {
        Span<short> residual = stackalloc short[16];
        for (var i = 0; i < 16; i++)
        {
            residual[i] = (short)(src16[i] - pred16[i]);
        }

        Span<int> coeff = stackalloc int[16];
        H264BlockTransform.ForwardDct4X4Scalar(residual, coeff);
        H264BlockTransform.Quant4X4Scalar(coeff, qp);

        var zz = H264Zigzag.Frame4X4;
        var nz = 0;
        for (var i = 0; i < 16; i++)
        {
            var v = coeff[zz[i]];
            zigzagCoeffOut16[i] = (short)v;
            if (v != 0)
            {
                nz++;
            }
        }

        Span<int> dequant = stackalloc int[16];
        H264BlockTransform.DequantAc4x4Spec(coeff, qp, dequant);

        Span<int> invRes = stackalloc int[16];
        H264BlockTransform.InverseDct4x4Spec(dequant, invRes);

        for (var rr = 0; rr < 4; rr++)
        {
            var recRow = rr * recStride;
            var rowOff = rr * 4;
            recDst[recRow + 0] = (byte)Math.Clamp(pred16[rowOff + 0] + invRes[rowOff + 0], 0, 255);
            recDst[recRow + 1] = (byte)Math.Clamp(pred16[rowOff + 1] + invRes[rowOff + 1], 0, 255);
            recDst[recRow + 2] = (byte)Math.Clamp(pred16[rowOff + 2] + invRes[rowOff + 2], 0, 255);
            recDst[recRow + 3] = (byte)Math.Clamp(pred16[rowOff + 3] + invRes[rowOff + 3], 0, 255);
        }

        return nz;
    }

    /// <summary>
    /// Same as <see cref="EncodeResidual4x4Scalar"/> but applies greedy trellis after quant.
    /// The reconstruction uses the trellis-modified coefficients.
    /// </summary>
    public static int EncodeResidual4x4Trellis(
        ReadOnlySpan<byte> src16,
        ReadOnlySpan<byte> pred16,
        int qp,
        Span<short> zigzagCoeffOut16,
        Span<byte> recDst,
        int recStride,
        int lambda2)
    {
        Span<short> residual = stackalloc short[16];
        for (var i = 0; i < 16; i++)
        {
            residual[i] = (short)(src16[i] - pred16[i]);
        }

        Span<int> coeff = stackalloc int[16];
        Span<int> preQuant = stackalloc int[16];
        H264BlockTransform.ForwardDct4X4Scalar(residual, coeff);
        coeff.CopyTo(preQuant);
        H264BlockTransform.Quant4X4Scalar(coeff, qp);

        var zz = H264Zigzag.Frame4X4;
        for (var i = 0; i < 16; i++)
        {
            zigzagCoeffOut16[i] = (short)coeff[zz[i]];
        }

        var nz = H264TrellisQuant4x4.Apply(zigzagCoeffOut16, preQuant, qp, lambda2);

        Span<int> zigzagInt = stackalloc int[16];
        for (var i = 0; i < 16; i++)
        {
            zigzagInt[i] = zigzagCoeffOut16[i];
        }

        H264BlockTransform.ZigzagToRaster(zigzagInt, coeff);

        Span<int> dequant = stackalloc int[16];
        H264BlockTransform.DequantAc4x4Spec(coeff, qp, dequant);

        Span<int> invRes = stackalloc int[16];
        H264BlockTransform.InverseDct4x4Spec(dequant, invRes);

        for (var rr = 0; rr < 4; rr++)
        {
            var recRow = rr * recStride;
            var rowOff = rr * 4;
            recDst[recRow + 0] = (byte)Math.Clamp(pred16[rowOff + 0] + invRes[rowOff + 0], 0, 255);
            recDst[recRow + 1] = (byte)Math.Clamp(pred16[rowOff + 1] + invRes[rowOff + 1], 0, 255);
            recDst[recRow + 2] = (byte)Math.Clamp(pred16[rowOff + 2] + invRes[rowOff + 2], 0, 255);
            recDst[recRow + 3] = (byte)Math.Clamp(pred16[rowOff + 3] + invRes[rowOff + 3], 0, 255);
        }

        return nz;
    }

    /// <summary>
    /// Fused SIMD bundle — bit-exact with <see cref="EncodeResidual4x4Scalar"/> when <see cref="IsSimdBundleSupported"/>.
    /// Keeps quantized coefficients in registers (four column vectors) through forward column-DCT + quant; only spills for inverse output.
    /// </summary>
    public static int EncodeResidual4x4Simd(
        ReadOnlySpan<byte> src16,
        ReadOnlySpan<byte> pred16,
        int qp,
        Span<short> zigzagCoeffOut16,
        Span<byte> recDst,
        int recStride)
    {
        if (!IsSimdBundleSupported)
        {
            throw new InvalidOperationException("EncodeResidual4x4Simd requires SSE4.1 or AdvSimd bundle ISA.");
        }

        qp = Math.Clamp(qp, 0, 51);
        var qbits = 15 + (qp / 6);
        var qpRem = qp % 6;
        var add = 1 << (qbits - 1);
        var sseQuant = Sse41.IsSupported;

        ButterflyResidualFourRows(src16, pred16, out var vb0, out var vb1, out var vb2, out var vb3);

        var mfCol0 = H264BlockTransformSimd.QuantMfPackedForSpectralColumnQuad(qpRem, 0);
        var mfCol1 = H264BlockTransformSimd.QuantMfPackedForSpectralColumnQuad(qpRem, 1);
        var mfCol2 = H264BlockTransformSimd.QuantMfPackedForSpectralColumnQuad(qpRem, 2);
        var mfCol3 = H264BlockTransformSimd.QuantMfPackedForSpectralColumnQuad(qpRem, 3);
        var qpPacked0 = sseQuant ? QuantVectorsPacked.ForSse(mfCol0, add, qbits) : QuantVectorsPacked.ForAdvSimd(mfCol0, add);
        var qpPacked1 = sseQuant ? QuantVectorsPacked.ForSse(mfCol1, add, qbits) : QuantVectorsPacked.ForAdvSimd(mfCol1, add);
        var qpPacked2 = sseQuant ? QuantVectorsPacked.ForSse(mfCol2, add, qbits) : QuantVectorsPacked.ForAdvSimd(mfCol2, add);
        var qpPacked3 = sseQuant ? QuantVectorsPacked.ForSse(mfCol3, add, qbits) : QuantVectorsPacked.ForAdvSimd(mfCol3, add);

        Transpose4X4Int32(vb0, vb1, vb2, vb3, out var col0, out var col1, out var col2, out var col3);
        var qc0 = QuantSingleVector(Butterfly(col0), qpPacked0, qbits, sseQuant);
        var qc1 = QuantSingleVector(Butterfly(col1), qpPacked1, qbits, sseQuant);
        var qc2 = QuantSingleVector(Butterfly(col2), qpPacked2, qbits, sseQuant);
        var qc3 = QuantSingleVector(Butterfly(col3), qpPacked3, qbits, sseQuant);

        if (VectorHasMinValue(qc0) || VectorHasMinValue(qc1) || VectorHasMinValue(qc2) || VectorHasMinValue(qc3))
        {
            return EncodeResidual4x4Scalar(src16, pred16, qp, zigzagCoeffOut16, recDst, recStride);
        }

        var nz = WriteZigzagCoeffNnUnrolled(qc0, qc1, qc2, qc3, zigzagCoeffOut16);

        // Vectorized dequant+IDCT reconstruction tail: stays in registers through to ReconResidual4X4.
        DequantIdct4x4SimdFromColumns(qc0, qc1, qc2, qc3, qp, pred16, recDst, recStride, sseQuant);

        return nz;
    }

    /// <summary>Scatter SIMD column quant regs to spectral raster (<see cref="WriteZigzagCoeffNnUnrolled"/> inverse).</summary>
    private static void CopyQuantSimdRegsToRaster(
        Vector128<int> qc0,
        Vector128<int> qc1,
        Vector128<int> qc2,
        Vector128<int> qc3,
        Span<int> qRaster)
    {
        qRaster[0] = qc0.GetElement(0);
        qRaster[1] = qc1.GetElement(0);
        qRaster[4] = qc0.GetElement(1);
        qRaster[8] = qc0.GetElement(2);
        qRaster[5] = qc1.GetElement(1);
        qRaster[2] = qc2.GetElement(0);
        qRaster[3] = qc3.GetElement(0);
        qRaster[6] = qc2.GetElement(1);
        qRaster[9] = qc1.GetElement(2);
        qRaster[12] = qc0.GetElement(3);
        qRaster[13] = qc1.GetElement(3);
        qRaster[10] = qc2.GetElement(2);
        qRaster[7] = qc3.GetElement(1);
        qRaster[11] = qc3.GetElement(2);
        qRaster[14] = qc2.GetElement(3);
        qRaster[15] = qc3.GetElement(3);
    }

    private readonly struct QuantVectorsPacked
    {
        public readonly Vector128<int> MfV;
        public readonly Vector128<int> AddV;
        public readonly Vector128<int> ShiftV;

        private QuantVectorsPacked(Vector128<int> mfV, Vector128<int> addV, Vector128<int> shiftV)
        {
            MfV = mfV;
            AddV = addV;
            ShiftV = shiftV;
        }

        public static QuantVectorsPacked ForSse(Vector128<int> mfV, int add, int qbits) =>
            new(mfV, Vector128.Create(add), Vector128.CreateScalar(qbits));

        public static QuantVectorsPacked ForAdvSimd(Vector128<int> mfV, int add) =>
            new(mfV, Vector128.Create(add), default);
    }

    /// <summary>No intermediate short spill; matches <c>(short)(src − pred)</c> widen used by scalar forward DCT.</summary>
    private static void ButterflyResidualFourRows(
        ReadOnlySpan<byte> src16,
        ReadOnlySpan<byte> pred16,
        out Vector128<int> vb0,
        out Vector128<int> vb1,
        out Vector128<int> vb2,
        out Vector128<int> vb3)
    {
        ref var sBase = ref MemoryMarshal.GetReference(src16);
        ref var pBase = ref MemoryMarshal.GetReference(pred16);
        vb0 = Butterfly(ReadDiffRow32(ref sBase, ref pBase, 0));
        vb1 = Butterfly(ReadDiffRow32(ref sBase, ref pBase, 4));
        vb2 = Butterfly(ReadDiffRow32(ref sBase, ref pBase, 8));
        vb3 = Butterfly(ReadDiffRow32(ref sBase, ref pBase, 12));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> ReadDiffRow32(ref byte sBase, ref byte pBase, nuint byteOffset)
    {
        var sRow = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref sBase, byteOffset));
        var pRow = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref pBase, byteOffset));
        if (Sse41.IsSupported)
        {
            var sv = Sse41.ConvertToVector128Int32(Vector128.CreateScalarUnsafe(sRow).AsByte());
            var pv = Sse41.ConvertToVector128Int32(Vector128.CreateScalarUnsafe(pRow).AsByte());
            return sv - pv;
        }

        var su = AdvSimd.ZeroExtendWideningLower(Vector128.CreateScalarUnsafe(sRow).AsByte().GetLower());
        var su32 = AdvSimd.ZeroExtendWideningLower(su.GetLower());
        var pu = AdvSimd.ZeroExtendWideningLower(Vector128.CreateScalarUnsafe(pRow).AsByte().GetLower());
        var pu32 = AdvSimd.ZeroExtendWideningLower(pu.GetLower());
        return su32.AsInt32() - pu32.AsInt32();
    }

    /// <summary>
    /// Zigzag order matches <see cref="H264Zigzag.Frame4X4"/> (fixed indices); no runtime raster lookup or column switch.
    /// </summary>
    private static int WriteZigzagCoeffNnUnrolled(
        Vector128<int> qc0,
        Vector128<int> qc1,
        Vector128<int> qc2,
        Vector128<int> qc3,
        Span<short> zigzagCoeffOut16)
    {
        if (Ssse3.IsSupported && Sse2.IsSupported)
        {
            var p01 = Sse2.PackSignedSaturate(qc0, qc1).AsByte();
            var p23 = Sse2.PackSignedSaturate(qc2, qc3).AsByte();

            var chunk0 = (Ssse3.Shuffle(p01, ZigzagShuffleA) | Ssse3.Shuffle(p23, ZigzagShuffleB)).AsInt16();
            var chunk1 = (Ssse3.Shuffle(p01, ZigzagShuffleC) | Ssse3.Shuffle(p23, ZigzagShuffleD)).AsInt16();

            ref var dst = ref MemoryMarshal.GetReference(zigzagCoeffOut16);
            chunk0.StoreUnsafe(ref dst);
            chunk1.StoreUnsafe(ref Unsafe.Add(ref dst, 8));

            var nz0 = int.PopCount((int)(~(uint)Vector128.Equals(chunk0, Vector128<short>.Zero).ExtractMostSignificantBits() & 0xFFu));
            var nz1 = int.PopCount((int)(~(uint)Vector128.Equals(chunk1, Vector128<short>.Zero).ExtractMostSignificantBits() & 0xFFu));
            return nz0 + nz1;
        }

        return WriteZigzagCoeffNnUnrolledScalar(qc0, qc1, qc2, qc3, zigzagCoeffOut16);
    }

    private static int WriteZigzagCoeffNnUnrolledScalar(
        Vector128<int> qc0,
        Vector128<int> qc1,
        Vector128<int> qc2,
        Vector128<int> qc3,
        Span<short> zigzagCoeffOut16)
    {
        ref var dst = ref MemoryMarshal.GetReference(zigzagCoeffOut16);
        var nz = 0;
        int v;

        v = qc0.GetElement(0);
        Unsafe.Add(ref dst, 0) = (short)v;
        nz += v != 0 ? 1 : 0;
        v = qc1.GetElement(0);
        Unsafe.Add(ref dst, 1) = (short)v;
        nz += v != 0 ? 1 : 0;
        v = qc0.GetElement(1);
        Unsafe.Add(ref dst, 2) = (short)v;
        nz += v != 0 ? 1 : 0;
        v = qc0.GetElement(2);
        Unsafe.Add(ref dst, 3) = (short)v;
        nz += v != 0 ? 1 : 0;
        v = qc1.GetElement(1);
        Unsafe.Add(ref dst, 4) = (short)v;
        nz += v != 0 ? 1 : 0;
        v = qc2.GetElement(0);
        Unsafe.Add(ref dst, 5) = (short)v;
        nz += v != 0 ? 1 : 0;
        v = qc3.GetElement(0);
        Unsafe.Add(ref dst, 6) = (short)v;
        nz += v != 0 ? 1 : 0;
        v = qc2.GetElement(1);
        Unsafe.Add(ref dst, 7) = (short)v;
        nz += v != 0 ? 1 : 0;
        v = qc1.GetElement(2);
        Unsafe.Add(ref dst, 8) = (short)v;
        nz += v != 0 ? 1 : 0;
        v = qc0.GetElement(3);
        Unsafe.Add(ref dst, 9) = (short)v;
        nz += v != 0 ? 1 : 0;
        v = qc1.GetElement(3);
        Unsafe.Add(ref dst, 10) = (short)v;
        nz += v != 0 ? 1 : 0;
        v = qc2.GetElement(2);
        Unsafe.Add(ref dst, 11) = (short)v;
        nz += v != 0 ? 1 : 0;
        v = qc3.GetElement(1);
        Unsafe.Add(ref dst, 12) = (short)v;
        nz += v != 0 ? 1 : 0;
        v = qc3.GetElement(2);
        Unsafe.Add(ref dst, 13) = (short)v;
        nz += v != 0 ? 1 : 0;
        v = qc2.GetElement(3);
        Unsafe.Add(ref dst, 14) = (short)v;
        nz += v != 0 ? 1 : 0;
        v = qc3.GetElement(3);
        Unsafe.Add(ref dst, 15) = (short)v;
        nz += v != 0 ? 1 : 0;

        return nz;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool VectorHasMinValue(Vector128<int> v) =>
        Vector128.Equals(v, Vector128.Create(int.MinValue)).ExtractMostSignificantBits() != 0;

    /// <summary>Matches one SIMD quant column-lane MF vector + shift.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> QuantSingleVector(Vector128<int> v, QuantVectorsPacked packed, int qbits, bool sse)
    {
        if (sse)
        {
            var mask = Sse2.ShiftRightArithmetic(v, 31);
            var ax = Sse2.Subtract(Sse2.Xor(v, mask), mask);
            var t = Sse2.Add(Sse41.MultiplyLow(ax, packed.MfV), packed.AddV);
            var qv = Sse2.ShiftRightArithmetic(t, packed.ShiftV);
            return Sse2.Subtract(Sse2.Xor(qv, mask), mask);
        }

        var mask2 = AdvSimd.ShiftRightArithmetic(v, 31);
        var ax2 = AdvSimd.Subtract(AdvSimd.Xor(v, mask2), mask2);
        var t2 = AdvSimd.Add(AdvSimd.Multiply(ax2, packed.MfV), packed.AddV);
#pragma warning disable CA1857 // shift amount from QP; AdvSimd API requires byte
        var qv2 = AdvSimd.ShiftRightArithmetic(t2, (byte)qbits);
#pragma warning restore CA1857
        return AdvSimd.Subtract(AdvSimd.Xor(qv2, mask2), mask2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreReconInt32Row(Vector128<int> clamped, ref byte dBase, int rowIndex, int recStride)
    {
        var rowOff = rowIndex * 4;
        if (recStride == 4)
        {
            if (Sse2.IsSupported)
            {
                var i16 = Sse2.PackSignedSaturate(clamped, Vector128<int>.Zero);
                var u8 = Sse2.PackUnsignedSaturate(i16, Vector128<short>.Zero);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dBase, rowOff), u8.AsUInt32().ToScalar());
            }
            else
            {
                Unsafe.Add(ref dBase, rowOff + 0) = (byte)clamped.GetElement(0);
                Unsafe.Add(ref dBase, rowOff + 1) = (byte)clamped.GetElement(1);
                Unsafe.Add(ref dBase, rowOff + 2) = (byte)clamped.GetElement(2);
                Unsafe.Add(ref dBase, rowOff + 3) = (byte)clamped.GetElement(3);
            }
        }
        else
        {
            var baseIdx = rowIndex * recStride;
            Unsafe.Add(ref dBase, baseIdx + 0) = (byte)clamped.GetElement(0);
            Unsafe.Add(ref dBase, baseIdx + 1) = (byte)clamped.GetElement(1);
            Unsafe.Add(ref dBase, baseIdx + 2) = (byte)clamped.GetElement(2);
            Unsafe.Add(ref dBase, baseIdx + 3) = (byte)clamped.GetElement(3);
        }
    }

    /// <summary>Clip pred + inverse residual into recon using SIMD min/max per row; packed UInt32 store when <paramref name="recStride"/> is 4.</summary>
    private static void ReconResidual4X4(ReadOnlySpan<byte> pred16, ReadOnlySpan<int> invRes, Span<byte> recDst, int recStride)
    {
        ref var pBase = ref MemoryMarshal.GetReference(pred16);
        ref var invBase = ref MemoryMarshal.GetReference(invRes);
        ref var dBase = ref MemoryMarshal.GetReference(recDst);

        var z = Vector128<int>.Zero;
        var hi = Vector128.Create(255);

        for (var rr = 0; rr < 4; rr++)
        {
            var rowOff = rr * 4;
            var predWide = PredByteRowToInt32(ref pBase, (nuint)(uint)rowOff);
            var invWide = Vector128.LoadUnsafe(ref Unsafe.Add(ref invBase, rowOff));
            var sum = predWide + invWide;
            var clamped = Vector128.Min(Vector128.Max(sum, z), hi);
            StoreReconInt32Row(clamped, ref dBase, rr, recStride);
        }
    }

    /// <summary>Reconstruction from four IDCT row vectors (avoids <c>stackalloc int[16]</c> spill).</summary>
    private static void ReconResidual4X4Vectors(
        ReadOnlySpan<byte> pred16,
        Vector128<int> r0,
        Vector128<int> r1,
        Vector128<int> r2,
        Vector128<int> r3,
        Span<byte> recDst,
        int recStride)
    {
        ref var pBase = ref MemoryMarshal.GetReference(pred16);
        ref var dBase = ref MemoryMarshal.GetReference(recDst);
        var z = Vector128<int>.Zero;
        var hi = Vector128.Create(255);

        var predWide0 = PredByteRowToInt32(ref pBase, 0);
        var clamped0 = Vector128.Min(Vector128.Max(predWide0 + r0, z), hi);
        StoreReconInt32Row(clamped0, ref dBase, 0, recStride);

        var predWide1 = PredByteRowToInt32(ref pBase, 4);
        var clamped1 = Vector128.Min(Vector128.Max(predWide1 + r1, z), hi);
        StoreReconInt32Row(clamped1, ref dBase, 1, recStride);

        var predWide2 = PredByteRowToInt32(ref pBase, 8);
        var clamped2 = Vector128.Min(Vector128.Max(predWide2 + r2, z), hi);
        StoreReconInt32Row(clamped2, ref dBase, 2, recStride);

        var predWide3 = PredByteRowToInt32(ref pBase, 12);
        var clamped3 = Vector128.Min(Vector128.Max(predWide3 + r3, z), hi);
        StoreReconInt32Row(clamped3, ref dBase, 3, recStride);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> PredByteRowToInt32(ref byte pBase, nuint byteOffset)
    {
        var prow = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref pBase, byteOffset));
        if (Sse41.IsSupported)
        {
            return Sse41.ConvertToVector128Int32(Vector128.CreateScalarUnsafe(prow).AsByte());
        }

        var pu = AdvSimd.ZeroExtendWideningLower(Vector128.CreateScalarUnsafe(prow).AsByte().GetLower());
        var pu32 = AdvSimd.ZeroExtendWideningLower(pu.GetLower());
        return pu32.AsInt32();
    }

    /// <summary>
    /// 4-point forward butterfly. Produces [s0+s1, 2*d0+d1, s0-s1, d0-2*d1] per H.264 §8.5.8.
    /// Matches <see cref="H264Dct4x4Simd.ForwardDct4X4"/> and <see cref="H264BlockTransform.ForwardDct4X4Scalar"/>.
    /// </summary>
    private static Vector128<int> Butterfly(Vector128<int> o)
    {
        var rev = Reverse4(o);
        var sum = o + rev;
        var diff = o - rev;

        // s0=sum[0], s1=sum[1], d0=diff[0], d1=diff[1]
        // b0=s0+s1, b1=2*d0+d1, b2=s0-s1, b3=d0-2*d1
        if (Sse2.IsSupported)
        {
            var s0b = Sse2.Shuffle(sum.AsSingle(), sum.AsSingle(), 0x00).AsInt32();
            var d0b = Sse2.Shuffle(diff.AsSingle(), diff.AsSingle(), 0x00).AsInt32();
            var s1b = Sse2.Shuffle(sum.AsSingle(), sum.AsSingle(), 0x55).AsInt32();
            var d1b = Sse2.Shuffle(diff.AsSingle(), diff.AsSingle(), 0x55).AsInt32();
            var d0s = Sse2.ShiftLeftLogical(d0b, 1);
            var d1s = Sse2.ShiftLeftLogical(d1b, 1);
            var b0 = s0b + s1b;
            var b1 = d0s + d1b;
            var b2 = s0b - s1b;
            var b3 = d0b - d1s;
            var lo = Sse2.UnpackLow(b0, b1);
            var hi = Sse2.UnpackLow(b2, b3);
            return Sse2.Shuffle(lo.AsSingle(), hi.AsSingle(), 0x44).AsInt32();
        }
        else
        {
            var s0b = AdvSimd.DuplicateSelectedScalarToVector128(sum, 0);
            var d0b = AdvSimd.DuplicateSelectedScalarToVector128(diff, 0);
            var s1b = AdvSimd.DuplicateSelectedScalarToVector128(sum, 1);
            var d1b = AdvSimd.DuplicateSelectedScalarToVector128(diff, 1);
            var d0s = d0b << 1;
            var d1s = d1b << 1;
            var b0 = s0b + s1b;
            var b1 = d0s + d1b;
            var b2 = s0b - s1b;
            var b3 = d0b - d1s;
            var lo = AdvSimd.Arm64.ZipLow(b0, b1);
            var hi = AdvSimd.Arm64.ZipLow(b2, b3);
            return AdvSimd.Arm64.ZipLow(lo.AsInt64(), hi.AsInt64()).AsInt32();
        }
    }

    private static Vector128<int> Reverse4(Vector128<int> o)
    {
        if (Sse2.IsSupported)
        {
            return Sse2.Shuffle(o.AsSingle(), o.AsSingle(), 0x1B).AsInt32();
        }

        return Vector128.Shuffle(o, Vector128.Create(3, 2, 1, 0));
    }

    // ── Vectorized dequant + IDCT reconstruction tail ────────────────────────────────────────────

    /// <summary>
    /// Public entry point for the SIMD dequant+IDCT reconstruction tail.
    /// Bit-exact with scalar <see cref="H264BlockTransform.DequantAc4x4Spec"/> +
    /// <see cref="H264BlockTransform.InverseDct4x4Spec"/> + <c>clip(pred+residual)</c>.
    /// Requires SSE4.1 or AdvSimd; throws <see cref="InvalidOperationException"/> otherwise.
    /// </summary>
    public static void DequantIdct4x4Scalar(
        ReadOnlySpan<int> qRaster16, int qp,
        ReadOnlySpan<byte> pred16,
        Span<byte> recDst, int recStride)
    {
        Span<int> dq = stackalloc int[16];
        H264BlockTransform.DequantAc4x4Spec(qRaster16, qp, dq);
        Span<int> invRes = stackalloc int[16];
        H264BlockTransform.InverseDct4x4Spec(dq, invRes);
        ReconResidual4X4(pred16, invRes, recDst, recStride);
    }

    public static void DequantIdct4x4Simd(
        ReadOnlySpan<int> qRaster16, int qp,
        ReadOnlySpan<byte> pred16,
        Span<byte> recDst, int recStride)
    {
        if (!IsSimdBundleSupported)
            throw new InvalidOperationException("DequantIdct4x4Simd requires SSE4.1 or AdvSimd.");

        if (qRaster16.Contains(int.MinValue))
        {
            DequantIdct4x4Scalar(qRaster16, qp, pred16, recDst, recStride);
            return;
        }

        qp = Math.Clamp(qp, 0, 51);
        // Build column vectors from raster input, then dispatch.
        var col0 = Vector128.Create(qRaster16[0],  qRaster16[4],  qRaster16[8],  qRaster16[12]);
        var col1 = Vector128.Create(qRaster16[1],  qRaster16[5],  qRaster16[9],  qRaster16[13]);
        var col2 = Vector128.Create(qRaster16[2],  qRaster16[6],  qRaster16[10], qRaster16[14]);
        var col3 = Vector128.Create(qRaster16[3],  qRaster16[7],  qRaster16[11], qRaster16[15]);
        DequantIdct4x4SimdFromColumns(col0, col1, col2, col3, qp, pred16, recDst, recStride,
            sse: Sse41.IsSupported);
    }

    /// <summary>
    /// Internal: dequant+IDCT from quantised coefficient column vectors (as produced by
    /// <see cref="EncodeResidual4x4Simd"/>'s forward quant). Avoids spilling to a raster
    /// array, keeping all 16 coefficients in SIMD registers through to
    /// <see cref="ReconResidual4X4"/>.
    /// </summary>
    private static void DequantIdct4x4SimdFromColumns(
        Vector128<int> col0, Vector128<int> col1, Vector128<int> col2, Vector128<int> col3,
        int qp,
        ReadOnlySpan<byte> pred16, Span<byte> recDst, int recStride,
        bool sse)
    {
        var div6 = qp / 6;
        var rem6 = qp % 6;

        // V-factor vectors per column (determined by raster slot pattern):
        //   Even columns (0, 2):  rows map to slots [0,1,0,1] → V=[Vaa, Vab, Vaa, Vab]
        //   Odd  columns (1, 3):  rows map to slots [1,2,1,2] → V=[Vab, Vbb, Vab, Vbb]
        var vaa = H264BlockTransform.SpecVAa[rem6];
        var vab = H264BlockTransform.SpecVAb[rem6];
        var vbb = H264BlockTransform.SpecVBb[rem6];
        var vColEven = Vector128.Create(vaa, vab, vaa, vab);
        var vColOdd  = Vector128.Create(vab, vbb, vab, vbb);

        // Dequant: d_col = q_col * V << div6  (logical left-shift is correct for signed 32-bit)
        Vector128<int> dc0, dc1, dc2, dc3;
        if (sse)
        {
#pragma warning disable CA1857 // shift amount from QP; Sse2 API requires byte
            var sh = (byte)div6;
            dc0 = Sse2.ShiftLeftLogical(Sse41.MultiplyLow(col0, vColEven), sh);
            dc1 = Sse2.ShiftLeftLogical(Sse41.MultiplyLow(col1, vColOdd),  sh);
            dc2 = Sse2.ShiftLeftLogical(Sse41.MultiplyLow(col2, vColEven), sh);
            dc3 = Sse2.ShiftLeftLogical(Sse41.MultiplyLow(col3, vColOdd),  sh);
#pragma warning restore CA1857
        }
        else
        {
            // AdvSimd.ShiftLogical: element-wise logical left shift by per-element count vector.
            var shiftVec = Vector128.Create(div6);
            dc0 = AdvSimd.ShiftLogical(AdvSimd.Multiply(col0, vColEven), shiftVec);
            dc1 = AdvSimd.ShiftLogical(AdvSimd.Multiply(col1, vColOdd),  shiftVec);
            dc2 = AdvSimd.ShiftLogical(AdvSimd.Multiply(col2, vColEven), shiftVec);
            dc3 = AdvSimd.ShiftLogical(AdvSimd.Multiply(col3, vColOdd),  shiftVec);
        }

        // IDCT row pass: transpose column→row, apply butterfly, transpose row→column.
        Transpose4X4Int32(dc0, dc1, dc2, dc3, out var r0, out var r1, out var r2, out var r3);
        r0 = InverseDctRowButterfly(r0, sse);
        r1 = InverseDctRowButterfly(r1, sse);
        r2 = InverseDctRowButterfly(r2, sse);
        r3 = InverseDctRowButterfly(r3, sse);
        Transpose4X4Int32(r0, r1, r2, r3, out dc0, out dc1, out dc2, out dc3);

        // IDCT column pass (with +32 >> 6 rounding).
        dc0 = InverseDctColumnButterfly(dc0, sse);
        dc1 = InverseDctColumnButterfly(dc1, sse);
        dc2 = InverseDctColumnButterfly(dc2, sse);
        dc3 = InverseDctColumnButterfly(dc3, sse);

        // Row-major IDCT output in registers → reconstruct without stack spill.
        Transpose4X4Int32(dc0, dc1, dc2, dc3, out r0, out r1, out r2, out r3);
        ReconResidual4X4Vectors(pred16, r0, r1, r2, r3, recDst, recStride);
    }

    /// <summary>
    /// H.264 §8.5.12.2 row butterfly for 1-D inverse DCT.
    /// Input [x0, x1, x2, x3] → [e0+e3, e1+e2, e1-e2, e0-e3]
    /// where e0=x0+x2, e1=x0-x2, e2=(x1>>1)-x3, e3=x1+(x3>>1).
    /// </summary>
    private static Vector128<int> InverseDctRowButterfly(Vector128<int> o, bool sse)
    {
        if (sse)
        {
            var x0b = Sse2.Shuffle(o.AsSingle(), o.AsSingle(), 0x00).AsInt32();
            var x1b = Sse2.Shuffle(o.AsSingle(), o.AsSingle(), 0x55).AsInt32();
            var x2b = Sse2.Shuffle(o.AsSingle(), o.AsSingle(), 0xAA).AsInt32();
            var x3b = Sse2.Shuffle(o.AsSingle(), o.AsSingle(), 0xFF).AsInt32();
            var e0b = x0b + x2b;
            var e1b = x0b - x2b;
            var e2b = Sse2.ShiftRightArithmetic(x1b, 1) - x3b;
            var e3b = x1b + Sse2.ShiftRightArithmetic(x3b, 1);
            var out0 = e0b + e3b;
            var out1 = e1b + e2b;
            var out2 = e1b - e2b;
            var out3 = e0b - e3b;
            var lo = Sse2.UnpackLow(out0, out1);
            var hi = Sse2.UnpackLow(out2, out3);
            return Sse2.Shuffle(lo.AsSingle(), hi.AsSingle(), 0x44).AsInt32();
        }
        else
        {
            var x0b = AdvSimd.DuplicateSelectedScalarToVector128(o, 0);
            var x1b = AdvSimd.DuplicateSelectedScalarToVector128(o, 1);
            var x2b = AdvSimd.DuplicateSelectedScalarToVector128(o, 2);
            var x3b = AdvSimd.DuplicateSelectedScalarToVector128(o, 3);
            var e0b = x0b + x2b;
            var e1b = x0b - x2b;
            var e2b = AdvSimd.ShiftRightArithmetic(x1b, 1) - x3b;
            var e3b = x1b + AdvSimd.ShiftRightArithmetic(x3b, 1);
            var out0 = e0b + e3b;
            var out1 = e1b + e2b;
            var out2 = e1b - e2b;
            var out3 = e0b - e3b;
            var lo = AdvSimd.Arm64.ZipLow(out0, out1);
            var hi = AdvSimd.Arm64.ZipLow(out2, out3);
            return AdvSimd.Arm64.ZipLow(lo.AsInt64(), hi.AsInt64()).AsInt32();
        }
    }

    /// <summary>
    /// H.264 §8.5.12.2 column butterfly for 1-D inverse DCT with <c>(f + 32) >> 6</c> rounding.
    /// Same formula as <see cref="InverseDctRowButterfly"/> but final outputs are rounded-shifted.
    /// </summary>
    private static Vector128<int> InverseDctColumnButterfly(Vector128<int> o, bool sse)
    {
        var add32 = Vector128.Create(32);
        if (sse)
        {
            var x0b = Sse2.Shuffle(o.AsSingle(), o.AsSingle(), 0x00).AsInt32();
            var x1b = Sse2.Shuffle(o.AsSingle(), o.AsSingle(), 0x55).AsInt32();
            var x2b = Sse2.Shuffle(o.AsSingle(), o.AsSingle(), 0xAA).AsInt32();
            var x3b = Sse2.Shuffle(o.AsSingle(), o.AsSingle(), 0xFF).AsInt32();
            var f0b = x0b + x2b;
            var f1b = x0b - x2b;
            var f2b = Sse2.ShiftRightArithmetic(x1b, 1) - x3b;
            var f3b = x1b + Sse2.ShiftRightArithmetic(x3b, 1);
            var r0 = Sse2.ShiftRightArithmetic(f0b + f3b + add32, 6);
            var r1 = Sse2.ShiftRightArithmetic(f1b + f2b + add32, 6);
            var r2 = Sse2.ShiftRightArithmetic(f1b - f2b + add32, 6);
            var r3 = Sse2.ShiftRightArithmetic(f0b - f3b + add32, 6);
            var lo = Sse2.UnpackLow(r0, r1);
            var hi = Sse2.UnpackLow(r2, r3);
            return Sse2.Shuffle(lo.AsSingle(), hi.AsSingle(), 0x44).AsInt32();
        }
        else
        {
            var x0b = AdvSimd.DuplicateSelectedScalarToVector128(o, 0);
            var x1b = AdvSimd.DuplicateSelectedScalarToVector128(o, 1);
            var x2b = AdvSimd.DuplicateSelectedScalarToVector128(o, 2);
            var x3b = AdvSimd.DuplicateSelectedScalarToVector128(o, 3);
            var f0b = x0b + x2b;
            var f1b = x0b - x2b;
            var f2b = AdvSimd.ShiftRightArithmetic(x1b, 1) - x3b;
            var f3b = x1b + AdvSimd.ShiftRightArithmetic(x3b, 1);
            var r0 = AdvSimd.ShiftRightArithmetic(f0b + f3b + add32, 6);
            var r1 = AdvSimd.ShiftRightArithmetic(f1b + f2b + add32, 6);
            var r2 = AdvSimd.ShiftRightArithmetic(f1b - f2b + add32, 6);
            var r3 = AdvSimd.ShiftRightArithmetic(f0b - f3b + add32, 6);
            var lo = AdvSimd.Arm64.ZipLow(r0, r1);
            var hi = AdvSimd.Arm64.ZipLow(r2, r3);
            return AdvSimd.Arm64.ZipLow(lo.AsInt64(), hi.AsInt64()).AsInt32();
        }
    }
}
