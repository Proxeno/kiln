namespace Kiln.Internal.H264;

/// <summary>
/// 4×4 luma DC Hadamard transform + quant per H.264 8.5.10. Used in Intra_16×16 macroblocks to
/// consolidate the 16 DC coefficients of the 16 4×4 luma sub-blocks before CAVLC encoding.
/// </summary>
/// <remarks>
/// <para>
/// The Hadamard matrix is symmetric (<c>Hᵀ = H</c>) so forward is <c>Y = H·X·H</c>; inverse is the
/// same shape and recovers <c>X</c> after dividing by 16 (because <c>H·H = 4·I</c>, applied twice).
/// The /16 normalization lives inside <see cref="InverseHadamard4x4"/> using <c>(z + 8) &gt;&gt; 4</c>
/// so the quant / dequant pair stays parallel to the chroma DC Hadamard family in
/// <see cref="H264BlockTransform"/> (<c>qbits = 15 + qp/6</c>, no extra Hadamard-scale shift).
/// </para>
/// <para>
/// Deliberately separate from <see cref="H264BlockTransform"/> so this code does not contend with
/// the chroma DC, IDCT, and quant routines other H.264 modules read from that file.
/// </para>
/// </remarks>
internal static class H264LumaDcHadamard
{
    /// <summary>4×4 forward Hadamard for the 16 luma DC coefficients (H.264 8.5.10).</summary>
    /// <remarks>
    /// Computes <c>Y = H · X · H</c> exactly, no scaling. Implemented as two 1-D butterfly passes
    /// (column then row) to avoid the 16-multiply-add loop of a literal matrix product. Output
    /// magnitude is ~16× input magnitude because <c>H · H = 4·I</c>.
    /// </remarks>
    public static void ForwardHadamard4x4(ReadOnlySpan<int> dc16, Span<int> dst16)
    {
        Span<int> tmp = stackalloc int[16];
        for (var col = 0; col < 4; col++)
        {
            var x0 = dc16[col];
            var x1 = dc16[4 + col];
            var x2 = dc16[8 + col];
            var x3 = dc16[12 + col];
            var b0 = x0 + x3;
            var b3 = x0 - x3;
            var b1 = x1 + x2;
            var b2 = x1 - x2;
            tmp[col] = b0 + b1;
            tmp[4 + col] = b3 + b2;
            tmp[8 + col] = b0 - b1;
            tmp[12 + col] = b3 - b2;
        }

        for (var row = 0; row < 4; row++)
        {
            var off = row * 4;
            var x0 = tmp[off + 0];
            var x1 = tmp[off + 1];
            var x2 = tmp[off + 2];
            var x3 = tmp[off + 3];
            var b0 = x0 + x3;
            var b3 = x0 - x3;
            var b1 = x1 + x2;
            var b2 = x1 - x2;
            dst16[off + 0] = b0 + b1;
            dst16[off + 1] = b3 + b2;
            dst16[off + 2] = b0 - b1;
            dst16[off + 3] = b3 - b2;
        }
    }

    /// <summary>Quantize the 16 Hadamard-domain DC coefficients per H.264 8.5.10.</summary>
    /// <remarks>
    /// Mirrors <see cref="H264BlockTransform.QuantChromaDcHadamard"/>'s scaling family: <c>qbits =
    /// 15 + qp/6</c>, <c>add = 1 &lt;&lt; (qbits - 1)</c> for unbiased rounding. The Hadamard's
    /// /16 magnitude factor is absorbed by <see cref="InverseHadamard4x4"/>, not by an extra qbits
    /// shift here.
    /// </remarks>
    public static void QuantLumaDcHadamard(Span<int> block16, int qp)
    {
        qp = Math.Clamp(qp, 0, 51);
        var qbits = 15 + (qp / 6);
        var mf = H264BlockTransform.MfHalvedAa[qp % 6];
        var add = 1 << (qbits - 1);
        for (var i = 0; i < 16; i++)
        {
            var v = block16[i];
            var sign = v < 0 ? -1 : 1;
            var abs = Math.Abs(v);
            var q = (abs * mf + add) >> qbits;
            block16[i] = sign * q;
        }
    }

    /// <summary>
    /// Arithmetic inverse of <see cref="QuantLumaDcHadamard"/> (a self-consistent quant↔dequant
    /// round-trip). WARNING: this is NOT the decoder's §8.5.10 dequant — it divides by the forward
    /// quant multiplier rather than multiplying by LevelScale, so feeding its output through the
    /// inverse Hadamard does not reproduce the decoder's reconstructed DC for non-zero residual. For
    /// encoder reconstruction that must match the decoder, use <see cref="ReconstructLumaDcFromQuant"/>.
    /// Retained only for the quant round-trip tests.
    /// </summary>
    public static void DequantLumaDcHadamard(Span<int> block16, int qp)
    {
        qp = Math.Clamp(qp, 0, 51);
        var qbits = 15 + (qp / 6);
        var mf = H264BlockTransform.MfHalvedAa[qp % 6];
        for (var i = 0; i < 16; i++)
        {
            var q = block16[i];
            if (q == 0)
            {
                continue;
            }

            var sign = q < 0 ? -1 : 1;
            var abs = Math.Abs(q);
            var dq = (abs << qbits) / mf;
            block16[i] = sign * dq;
        }
    }

    /// <summary>4×4 inverse Hadamard for the 16 luma DC coefficients (H.264 8.5.10).</summary>
    /// <remarks>
    /// Computes <c>Z = H · Y · H</c> with the same butterfly as the forward pass (H is symmetric)
    /// and then divides by 16 with arithmetic shift <c>(z + 8) &gt;&gt; 4</c>. The arithmetic shift
    /// is critical: a plain <c>/16</c> truncates toward zero and produces +1 errors on negative
    /// reconstructions, breaking the round-trip on signed inputs.
    /// </remarks>
    public static void InverseHadamard4x4(ReadOnlySpan<int> w16, Span<int> dst16)
    {
        Span<int> tmp = stackalloc int[16];
        for (var col = 0; col < 4; col++)
        {
            var x0 = w16[col];
            var x1 = w16[4 + col];
            var x2 = w16[8 + col];
            var x3 = w16[12 + col];
            var b0 = x0 + x3;
            var b3 = x0 - x3;
            var b1 = x1 + x2;
            var b2 = x1 - x2;
            tmp[col] = b0 + b1;
            tmp[4 + col] = b3 + b2;
            tmp[8 + col] = b0 - b1;
            tmp[12 + col] = b3 - b2;
        }

        for (var row = 0; row < 4; row++)
        {
            var off = row * 4;
            var x0 = tmp[off + 0];
            var x1 = tmp[off + 1];
            var x2 = tmp[off + 2];
            var x3 = tmp[off + 3];
            var b0 = x0 + x3;
            var b3 = x0 - x3;
            var b1 = x1 + x2;
            var b2 = x1 - x2;
            dst16[off + 0] = (b0 + b1 + 8) >> 4;
            dst16[off + 1] = (b3 + b2 + 8) >> 4;
            dst16[off + 2] = (b0 - b1 + 8) >> 4;
            dst16[off + 3] = (b3 - b2 + 8) >> 4;
        }
    }

    /// <summary>
    /// Reconstructs the 16 Intra_16x16 luma DC residual coefficients from the quantised DC levels in
    /// raster order (the values written to the bitstream), reproducing ITU-T H.264 (ISO/IEC 14496-10)
    /// clause 8.5.10 step for step: the un-normalised inverse 4×4 Hadamard <c>f = A · c · A</c>,
    /// then the DC level scale <c>LevelScale4x4(qP%6, 0, 0) = weightScale4x4(0,0) ×
    /// normAdjust4x4(qP%6, (0,0)) = 16 × v[qP%6]</c> with a flat scaling list, and finally the clause's
    /// two-branch shift — <c>&lt;&lt; (qP/6 − 6)</c> for qP ≥ 36 and the rounded
    /// <c>&gt;&gt; (6 − qP/6)</c> below that. The result is therefore bit-exact against a conforming
    /// decoder. The output is in the same scaled-coefficient
    /// domain as <see cref="H264BlockTransform.DequantAc4x4Spec"/>, so each value can be injected at
    /// position 0 of its 4×4 block before <see cref="H264BlockTransform.InverseDct4x4Spec"/>.
    /// <para>
    /// This replaces the earlier round-trip dequant (<see cref="DequantLumaDcHadamard"/> +
    /// <see cref="InverseHadamard4x4"/>), which inverted the encoder's own quant rather than
    /// reproducing the decoder's spec arithmetic and therefore drifted on any non-zero DC residual.
    /// </para>
    /// </summary>
    public static void ReconstructLumaDcFromQuant(ReadOnlySpan<int> quantHadRaster, int qp, Span<int> reconDc16)
    {
        qp = Math.Clamp(qp, 0, 51);

        // §8.5.10: inverse 4×4 Hadamard with NO normalisation; the /16 is folded into the dequant
        // scale (LevelScale already carries the weightScale factor of 16) and the shift below.
        Span<int> f = stackalloc int[16];
        InverseHadamard4x4NoNorm(quantHadRaster, f);

        var div6 = qp / 6;
        var rem6 = qp % 6;
        var levelScale = 16 * H264BlockTransform.SpecVAa[rem6]; // LevelScale4x4(qP%6, 0, 0), flat scaling list
        if (div6 >= 6)
        {
            var shift = div6 - 6;
            for (var i = 0; i < 16; i++) reconDc16[i] = (f[i] * levelScale) << shift;
        }
        else
        {
            var shift = 6 - div6;
            var round = 1 << (shift - 1); // = 1 << (5 − qP/6)
            for (var i = 0; i < 16; i++) reconDc16[i] = (f[i] * levelScale + round) >> shift;
        }
    }

    /// <summary>Inverse 4×4 Hadamard butterfly (column then row) with no normalisation — the
    /// transform <c>H·c·H</c> per §8.5.10, leaving the magnitude factor to the dequant scale.</summary>
    private static void InverseHadamard4x4NoNorm(ReadOnlySpan<int> w16, Span<int> dst16)
    {
        Span<int> tmp = stackalloc int[16];
        for (var col = 0; col < 4; col++)
        {
            var x0 = w16[col];
            var x1 = w16[4 + col];
            var x2 = w16[8 + col];
            var x3 = w16[12 + col];
            var b0 = x0 + x3;
            var b3 = x0 - x3;
            var b1 = x1 + x2;
            var b2 = x1 - x2;
            tmp[col] = b0 + b1;
            tmp[4 + col] = b3 + b2;
            tmp[8 + col] = b0 - b1;
            tmp[12 + col] = b3 - b2;
        }

        for (var row = 0; row < 4; row++)
        {
            var off = row * 4;
            var x0 = tmp[off + 0];
            var x1 = tmp[off + 1];
            var x2 = tmp[off + 2];
            var x3 = tmp[off + 3];
            var b0 = x0 + x3;
            var b3 = x0 - x3;
            var b1 = x1 + x2;
            var b2 = x1 - x2;
            dst16[off + 0] = b0 + b1;
            dst16[off + 1] = b3 + b2;
            dst16[off + 2] = b0 - b1;
            dst16[off + 3] = b3 - b2;
        }
    }
}
