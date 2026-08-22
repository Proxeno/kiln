using System.Runtime.CompilerServices;

namespace Kiln.Internal.H264;

internal static class H264Zigzag
{
    public static ReadOnlySpan<byte> Frame4X4 =>
    [
        0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15,
    ];
}

/// <summary>Integer 4×4 DCT path + quant (Kiln butterfly), plus approximate reconstruction.</summary>
internal static class H264BlockTransform
{
    /// <summary>
    /// Forward-quantisation multipliers MF, position class (0,0), indexed by qP % 6. These are the
    /// integer reciprocals of the ITU-T H.264 clause 8.5.9 normAdjust4x4 scaling that this encoder uses
    /// on the analysis side; they are an encoder-side choice, not specification data, since ITU-T H.264
    /// normatively defines only the inverse (dequantisation) direction. Halved (×½) here to pair with
    /// this encoder's forward column-pass gain of 2.
    /// </summary>
    internal static ReadOnlySpan<int> MfHalvedAa =>
        [13107 >> 1, 11916 >> 1, 10082 >> 1, 9362 >> 1, 8192 >> 1, 7282 >> 1];

    internal static ReadOnlySpan<int> MfHalvedBb =>
        [5243 >> 1, 4660 >> 1, 4194 >> 1, 3647 >> 1, 3355 >> 1, 2893 >> 1];

    internal static ReadOnlySpan<int> MfHalvedAb =>
        [8066 >> 1, 7490 >> 1, 6554 >> 1, 5825 >> 1, 5243 >> 1, 4559 >> 1];

    /// <summary>Un-halved forward-quant multiplier factors (encoder-side reciprocal of the clause 8.5.9 dequant scale; the spec defines only the inverse direction). Used by the reference quant-formula checks.</summary>
    internal static ReadOnlySpan<int> FullMfAa =>
        [13107, 11916, 10082, 9362, 8192, 7282];

    internal static ReadOnlySpan<int> FullMfBb =>
        [5243, 4660, 4194, 3647, 3355, 2893];

    internal static ReadOnlySpan<int> FullMfAb =>
        [8066, 7490, 6554, 5825, 5243, 4559];

    /// <summary>
    /// Position class of a 4×4 raster index under ITU-T H.264 clause 8.5.9. normAdjust4x4(m, (i, j))
    /// takes one of three values selected purely by the parities of i and j: <c>v[m][0]</c> when both are
    /// even, <c>v[m][1]</c> when both are odd, and <c>v[m][2]</c> otherwise. For a raster index r,
    /// <c>r &amp; 1</c> is the column parity and <c>(r &gt;&gt; 2) &amp; 1</c> the row parity, so their sum
    /// gives 0 for the both-even class, 2 for the both-odd class and 1 for the mixed class.
    /// </summary>
    internal static byte NormAdjustPositionClass(int rasterIdx16) =>
        (byte)((rasterIdx16 & 1) + ((rasterIdx16 >> 2) & 1));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MfHalvedForLumaRasterIndex(int qpRem6, int rasterIdx16) =>
        NormAdjustPositionClass(rasterIdx16) switch
        {
            0 => MfHalvedAa[qpRem6],
            1 => MfHalvedAb[qpRem6],
            _ => MfHalvedBb[qpRem6],
        };

    /// <summary>Full (un-halved) forward-quant MF by <see cref="NormAdjustPositionClass"/> (audit / reference quant path).</summary>
    internal static int FullMfForRasterIndex(int qpRem6, int rasterIdx16) =>
        NormAdjustPositionClass(rasterIdx16) switch
        {
            0 => FullMfAa[qpRem6],
            1 => FullMfAb[qpRem6],
            _ => FullMfBb[qpRem6],
        };

    /// <summary>
    /// normAdjust4x4(m, (i, j)) from ITU-T H.264 clause 8.5.9, split by
    /// <see cref="NormAdjustPositionClass"/>: <c>SpecVAa</c> is the both-even column <c>v[m][0]</c>,
    /// <c>SpecVAb</c> the mixed-parity column <c>v[m][2]</c>, and <c>SpecVBb</c> the both-odd column
    /// <c>v[m][1]</c>, each indexed by m = qP % 6.
    /// </summary>
    internal static ReadOnlySpan<int> SpecVAa => [10, 11, 13, 14, 16, 18];

    internal static ReadOnlySpan<int> SpecVAb => [13, 14, 16, 18, 20, 23];

    internal static ReadOnlySpan<int> SpecVBb => [16, 18, 20, 23, 25, 29];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int SpecVForLumaRasterIndex(int qpRem6, int rasterIdx16) =>
        NormAdjustPositionClass(rasterIdx16) switch
        {
            0 => SpecVAa[qpRem6],
            1 => SpecVAb[qpRem6],
            _ => SpecVBb[qpRem6],
        };

    /// <summary>
    /// ITU-T H.264 clause 8.5.12.1 inverse scaling for 4×4 AC coefficients (luma and chroma, intra and
    /// inter), raster order.
    /// </summary>
    /// <remarks>
    /// Computed here as <c>d[i] = level × normAdjust4x4(qP%6, i) &lt;&lt; (qP/6)</c>, which is exactly the
    /// clause 8.5.12.1 result for a flat scaling list: LevelScale4x4 = weightScale4x4 × normAdjust4x4 =
    /// 16 × normAdjust4x4, and the clause's shifts (<c>&lt;&lt; (qP/6 − 4)</c> for qP ≥ 24,
    /// <c>&gt;&gt; (4 − qP/6)</c> with a rounding offset below that) absorb the factor of 16 without
    /// changing the value. Working in normAdjust units rather than LevelScale units is this encoder's
    /// own convention and keeps the intermediate magnitudes small.
    /// </remarks>
    public static void DequantAc4x4Spec(ReadOnlySpan<int> q, int qp, Span<int> d)
    {
        qp = Math.Clamp(qp, 0, 51);
        var div6 = qp / 6;
        var rem6 = qp % 6;
        for (var i = 0; i < 16; i++)
        {
            d[i] = q[i] * SpecVForLumaRasterIndex(rem6, i) << div6;
        }
    }

    /// <summary>H.264 §8.5.12.2 canonical 4×4 inverse transform (<c>c<sub>IJ</sub></c>), raster coefficients.</summary>
    public static void InverseDct4x4Spec(ReadOnlySpan<int> dequantRaster, Span<int> residual16)
    {
        Span<int> t = stackalloc int[16];
        for (var r = 0; r < 4; r++)
        {
            var o = r * 4;
            var e0 = dequantRaster[o + 0] + dequantRaster[o + 2];
            var e1 = dequantRaster[o + 0] - dequantRaster[o + 2];
            var e2 = (dequantRaster[o + 1] >> 1) - dequantRaster[o + 3];
            var e3 = dequantRaster[o + 1] + (dequantRaster[o + 3] >> 1);
            t[o + 0] = e0 + e3;
            t[o + 1] = e1 + e2;
            t[o + 2] = e1 - e2;
            t[o + 3] = e0 - e3;
        }

        for (var c = 0; c < 4; c++)
        {
            var f0 = t[c + 0] + t[c + 8];
            var f1 = t[c + 0] - t[c + 8];
            var f2 = (t[c + 4] >> 1) - t[c + 12];
            var f3 = t[c + 4] + (t[c + 12] >> 1);
            residual16[c + 0] = (f0 + f3 + 32) >> 6;
            residual16[c + 4] = (f1 + f2 + 32) >> 6;
            residual16[c + 8] = (f1 - f2 + 32) >> 6;
            residual16[c + 12] = (f0 - f3 + 32) >> 6;
        }
    }

    /// <summary>Fixed-point 4×4 inverse DCT matrix multiply (÷1024). For encoder reconstruction after <see cref="DequantApprox"/> (which returns 2×C_f from spec q), use <see cref="InverseDctMatrixMultiplyEncoderRecon"/> so M·(4C_f)/1024 = residual (M·2C_f = 512·I).</summary>
    internal static ReadOnlySpan<short> InverseDctMatrixCoefficients => InverseDctMatrix;

    /// <summary>Widened <see cref="InverseDctMatrixCoefficients"/> for SIMD inverse multiply (no per-tap short→int widen).</summary>
    internal static ReadOnlySpan<int> InverseDctMatrixCoefficientsInt32 => InverseDctMatrixInt32;

    private static ReadOnlySpan<short> InverseDctMatrix =>
    [
        16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 8, -16, -8, 16, 8, -16, -8, 16, 8, -16, -8, 16, 8, -16, -8, 16, -8, -16, 8, 16, -8, -16, 8, 16, -8, -16, 8, 16, -8, -16, 8, 16, -16, 16, -16, 16, -16, 16, -16, 16, -16, 16, -16, 16, -16, 16, -16,
        16, 16, 16, 16, 8, 8, 8, 8, -16, -16, -16, -16, -8, -8, -8, -8, 16, 8, -16, -8, 8, 4, -8, -4, -16, -8, 16, 8, -8, -4, 8, 4, 16, -8, -16, 8, 8, -4, -8, 4, -16, 8, 16, -8, -8, 4, 8, -4, 16, -16, 16, -16, 8, -8, 8, -8, -16, 16, -16, 16, -8, 8, -8, 8, 16, 16, 16, 16, -8, -8, -8, -8, -16, -16, -16, -16, 8, 8, 8, 8, 16, 8, -16, -8, -8, -4, 8, 4, -16, -8, 16, 8, 8, 4, -8, -4, 16, -8, -16, 8, -8, 4, 8, -4, -16, 8, 16, -8, 8, -4, -8, 4, 16, -16, 16, -16, -8, 8, -8, 8, -16, 16, -16, 16, 8, -8, 8, -8, 16, 16, 16, 16, -16, -16, -16, -16, 16, 16, 16, 16, -16, -16, -16, -16, 16, 8, -16, -8, -16, -8, 16, 8, 16, 8, -16, -8, -16, -8, 16, 8, 16, -8, -16, 8, -16, 8, 16, -8, 16, -8, -16, 8, -16, 8, 16, -8,         16, -16, 16, -16, -16, 16, -16, 16, 16, -16, 16, -16, -16, 16, -16, 16,
    ];

    private static readonly int[] InverseDctMatrixInt32 = InitializeInverseDctMatrixInt32();

    private static int[] InitializeInverseDctMatrixInt32()
    {
        ReadOnlySpan<short> s = InverseDctMatrix;
        var dst = new int[256];
        for (var i = 0; i < 256; i++)
        {
            dst[i] = s[i];
        }

        return dst;
    }

    public static void DequantApprox(ReadOnlySpan<int> quantCoeff, int qp, Span<int> fwdDomain)
    {
        qp = Math.Clamp(qp, 0, 51);
        var qbits = 15 + (qp / 6);
        var qpRem = qp % 6;
        for (var i = 0; i < 16; i++)
        {
            var q = quantCoeff[i];
            if (q == 0)
            {
                fwdDomain[i] = 0;
                continue;
            }

            var sign = q < 0 ? -1 : 1;
            var abs = Math.Abs(q);
            var mf = MfHalvedForLumaRasterIndex(qpRem, i);
            fwdDomain[i] = sign * ((abs << qbits) / mf);
        }
    }

    /// <summary>4×4 IDCT for encoder reconstruction after inverse quant (doubles pre-multiply for 2× forward DCT scale).</summary>
    public static void InverseDctMatrixMultiplyEncoderRecon(ReadOnlySpan<int> dequantRaster, Span<int> residual16)
    {
        Span<int> doubled = stackalloc int[16];
        for (var i = 0; i < 16; i++)
        {
            doubled[i] = dequantRaster[i] << 1;
        }

        InverseDctMatrixMultiply(doubled, residual16);
    }

    public static void InverseDctMatrixMultiply(ReadOnlySpan<int> fwdDomain, Span<int> residual16)
    {
        if (H264IntrinsicsPreference.UseDctSimd)
        {
            H264Dct4x4Simd.InverseDctMatrixMultiply(InverseDctMatrixCoefficientsInt32, fwdDomain, residual16);
            return;
        }

        InverseDctMatrixMultiplyScalar(fwdDomain, residual16);
    }

    /// <summary>Scalar inverse DCT (reference for SIMD parity tests).</summary>
    internal static void InverseDctMatrixMultiplyScalar(ReadOnlySpan<int> fwdDomain, Span<int> residual16)
    {
        var m = InverseDctMatrix;
        const int scale = 1024;
        for (var row = 0; row < 16; row++)
        {
            long sum = 0;
            var off = row * 16;
            for (var col = 0; col < 16; col++)
            {
                sum += m[off + col] * fwdDomain[col];
            }

            var v = (int)(sum / scale);
            residual16[row] = v;
        }
    }

    public static void ForwardDct4X4(ReadOnlySpan<short> residual4X4, Span<int> outCoeff)
    {
        if (H264IntrinsicsPreference.UseDctSimd)
        {
            H264Dct4x4Simd.ForwardDct4X4(residual4X4, outCoeff);
            return;
        }

        ForwardDct4X4Scalar(residual4X4, outCoeff);
    }

    /// <summary>Scalar forward 4×4 DCT (reference for SIMD parity tests).</summary>
    internal static void ForwardDct4X4Scalar(ReadOnlySpan<short> residual4X4, Span<int> outCoeff)
    {
        Span<int> b = stackalloc int[16];
        for (var r = 0; r < 4; r++)
        {
            var o = r * 4;
            var e0 = residual4X4[o + 0] + residual4X4[o + 3];
            var e1 = residual4X4[o + 1] + residual4X4[o + 2];
            var e2 = residual4X4[o + 1] - residual4X4[o + 2];
            var e3 = residual4X4[o + 0] - residual4X4[o + 3];
            b[o + 0] = e0 + e1;
            b[o + 1] = (e3 << 1) + e2;
            b[o + 2] = e0 - e1;
            b[o + 3] = e3 - (e2 << 1);
        }

        for (var c = 0; c < 4; c++)
        {
            var e0 = b[c + 0] + b[c + 12];
            var e1 = b[c + 4] + b[c + 8];
            var e2 = b[c + 4] - b[c + 8];
            var e3 = b[c + 0] - b[c + 12];
            var f0 = e0 + e1;
            var f1 = (e3 << 1) + e2;
            var f2 = e0 - e1;
            var f3 = e3 - (e2 << 1);
            outCoeff[c + 0] = f0;
            outCoeff[c + 4] = f1;
            outCoeff[c + 8] = f2;
            outCoeff[c + 12] = f3;
        }
    }

    public static void Quant4X4(Span<int> block, int qp)
    {
        if (H264IntrinsicsPreference.UseQuantSimd)
        {
            H264BlockTransformSimd.Quant4X4(block, qp);
            return;
        }

        Quant4X4Scalar(block, qp);
    }

    /// <summary>Scalar quant (reference for SIMD parity tests).</summary>
    internal static void Quant4X4Scalar(Span<int> block, int qp)
    {
        qp = Math.Clamp(qp, 0, 51);
        var qbits = 15 + (qp / 6);
        var qpRem = qp % 6;
        var add = 1 << (qbits - 1);
        for (var i = 0; i < 16; i++)
        {
            var v = block[i];
            var sign = v < 0 ? -1 : 1;
            var abs = Math.Abs(v);
            var mf = FullMfForRasterIndex(qpRem, i);
            var q = (abs * mf + add) >> qbits;
            block[i] = sign * q;
        }
    }

    public static void RasterToZigzag(ReadOnlySpan<int> raster, Span<int> zigZag)
    {
        var zz = H264Zigzag.Frame4X4;
        for (var i = 0; i < 16; i++)
        {
            zigZag[i] = raster[zz[i]];
        }
    }

    public static void ZigzagToRaster(ReadOnlySpan<int> zigZag, Span<int> raster)
    {
        var zz = H264Zigzag.Frame4X4;
        for (var i = 0; i < 16; i++)
        {
            raster[zz[i]] = zigZag[i];
        }
    }

    public static void CopyZigzagToShort(ReadOnlySpan<int> zigZag, Span<short> dst)
    {
        for (var i = 0; i < 16; i++)
        {
            dst[i] = (short)zigZag[i];
        }
    }

    /// <summary>4:2:0 chroma DC Hadamard forward (H.264 8.5.5); output fed to quant then CAVLC ChromaDc.</summary>
    public static void ChromaDcHadamardForward(ReadOnlySpan<int> dc4, Span<int> dst)
    {
        var a0 = dc4[0];
        var a1 = dc4[1];
        var a2 = dc4[2];
        var a3 = dc4[3];
        dst[0] = a0 + a1 + a2 + a3;
        dst[1] = a0 - a1 + a2 - a3;
        dst[2] = a0 + a1 - a2 - a3;
        dst[3] = a0 - a1 - a2 + a3;
    }

    /// <summary>Inverse Hadamard for encoder-side reconstructed chroma DCs after dequant.</summary>
    public static void ChromaDcHadamardInverse(ReadOnlySpan<int> w4, Span<int> dst)
    {
        var a0 = w4[0];
        var a1 = w4[1];
        var a2 = w4[2];
        var a3 = w4[3];
        dst[0] = a0 + a1 + a2 + a3;
        dst[1] = a0 - a1 + a2 - a3;
        dst[2] = a0 + a1 - a2 - a3;
        dst[3] = a0 - a1 - a2 + a3;
        for (var i = 0; i < 4; i++)
        {
            dst[i] = (dst[i] + 2) >> 2;
        }
    }

    /// <summary>Quantize the 4 chroma WHT DC values (same scaling family as <see cref="Quant4X4"/>).</summary>
    public static void QuantChromaDcHadamard(Span<int> block, int qp)
    {
        qp = Math.Clamp(qp, 0, 51);
        var qbits = 15 + (qp / 6);
        var mf = MfHalvedAa[qp % 6];
        var add = 1 << (qbits - 1);
        for (var i = 0; i < 4; i++)
        {
            var v = block[i];
            var sign = v < 0 ? -1 : 1;
            var abs = Math.Abs(v);
            var q = (abs * mf + add) >> qbits;
            block[i] = sign * q;
        }
    }

    /// <summary>Dequant chroma WHT DC coefficients (pairs with <see cref="QuantChromaDcHadamard"/>).</summary>
    public static void DequantChromaDcHadamard(Span<int> block, int qp)
    {
        qp = Math.Clamp(qp, 0, 51);
        var qbits = 15 + (qp / 6);
        var mf = MfHalvedAa[qp % 6];
        for (var i = 0; i < 4; i++)
        {
            var q = block[i];
            if (q == 0)
            {
                continue;
            }

            var sign = q < 0 ? -1 : 1;
            var abs = Math.Abs(q);
            var dq = (abs << qbits) / mf;
            block[i] = sign * dq;
        }
    }
}
