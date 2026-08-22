using System.Numerics;

namespace Kiln.Internal.H264;

/// <summary>
/// Greedy per-coefficient trellis quantization for 4×4 luma/chroma blocks.
/// For each coefficient in zigzag order, evaluates keep(q), demote(|q|−1,sign), zero(0)
/// and picks the candidate minimising D + λ·R in DCT-domain units.
/// </summary>
internal static class H264TrellisQuant4x4
{
    /// <summary>
    /// Trellis λ in λ² integer scale (multiply by fixed-point λ² expressed as ×256 in <paramref name="lambda2"/> cost).
    /// </summary>
    public static int LambdaForQp(int qp) =>
        Math.Max(1, (int)(Math.Pow(2.0, (qp - 12) / 6.0) * 256 + 0.5));

    /// <param name="zigzagCoeff">16 zigzag-ordered quantised coefficients (modified in-place).</param>
    /// <param name="forwardCoeff">16 raster-ordered pre-quantisation DCT coefficients (read-only; used for D term).</param>
    /// <param name="qp">Luma QP [0,51].</param>
    /// <param name="lambda2">Trellis lambda (λ² scale, integer fixed-point × 256).</param>
    /// <returns>New non-zero count after trellis.</returns>
    public static int Apply(Span<short> zigzagCoeff, ReadOnlySpan<int> forwardCoeff, int qp, int lambda2)
    {
        if (zigzagCoeff.Length != 16 || forwardCoeff.Length != 16)
        {
            throw new ArgumentException("Expected spans of length 16.");
        }

        qp = Math.Clamp(qp, 0, 51);
        var div6 = qp / 6;
        var rem6 = qp % 6;
        var zz = H264Zigzag.Frame4X4;

        for (var z = 0; z < 16; z++)
        {
            var q = zigzagCoeff[z];
            var r = zz[z];
            var orig = (long)forwardCoeff[r];

            short bestCand = q;
            var bestCost = long.MaxValue;

            void Consider(short cand)
            {
                var dq = DequantCandidate(cand, rem6, div6, r);
                var d = orig - dq;
                var dist = d * d;
                var cost = dist + (long)lambda2 * ApproxRateBits(cand);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestCand = cand;
                }
            }

            Consider(q);

            var absQ = Math.Abs((int)q);
            if (absQ != 0)
            {
                if (absQ != 1)
                {
                    var dem = (short)(q > 0 ? q - 1 : q + 1);
                    Consider(dem);
                }
                Consider(0);
            }

            zigzagCoeff[z] = bestCand;
        }

        var nz = 0;
        for (var i = 0; i < 16; i++)
        {
            if (zigzagCoeff[i] != 0)
            {
                nz++;
            }
        }

        return nz;
    }

    private static long DequantCandidate(int level, int rem6, int div6, int rasterIdx16) =>
        (long)level * H264BlockTransform.SpecVForLumaRasterIndex(rem6, rasterIdx16) << div6;

    /// <summary>Approximate marginal CAVLC bits for coefficient magnitude (relative comparisons only).</summary>
    internal static int ApproxRateBits(short level)
    {
        if (level == 0)
        {
            return 0;
        }

        var abs = Math.Abs((int)level);
        if (abs == 1)
        {
            return 3;
        }

        var uAbs = abs - 1;
        var bitLen = BitOperations.Log2((uint)uAbs) + 1;
        return 4 + bitLen;
    }
}
