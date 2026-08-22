using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>Deterministic checks for §8.5.10/12.2 helper paths (paired with Kiln Route-A quant levels).</summary>
public sealed class H264SpecInverse4x4ParityTests
{
    [Fact]
    public void NormAdjustPositionClass_matches_parity_group_index()
    {
        for (var i = 0; i < 16; i++)
        {
            var expected = (byte)((i & 1) + ((i >> 2) & 1));
            H264BlockTransform.NormAdjustPositionClass(i).Should().Be(expected);
        }
    }

    [Fact]
    public void DequantAc4x4Spec_qp0_matches_level_scale_formula()
    {
        // Correct formula: d = level × LevelScale4x4(QP%6, pos) × 2^(QP/6)
        // LevelScale4x4(qp, i) = normAdjust4x4(qp%6, i) << (qp/6) for the flat scaling list (clause 8.5.9).
        Span<int> q = stackalloc int[16];
        Span<int> d = stackalloc int[16];
        q[0] = 5;
        const int qp = 0;
        var v = H264BlockTransform.SpecVAa[qp % 6];
        H264BlockTransform.DequantAc4x4Spec(q, qp, d);
        var expected = 5 * v << (qp / 6);  // 5 * 10 * 1 = 50
        d[0].Should().Be(expected);
        d[0].Should().Be(50);
    }

    [Theory]
    [InlineData(22)]
    [InlineData(28)]
    public void Kiln_forward_then_quant_then_spec_inverse_tracks_residual(int qp)
    {
        // Representative residual block; values fit well within the ±255 inter-frame range.
        Span<short> residual = stackalloc short[16]
        {
            10, -20,  30, -40,
            50, -60,  70, -80,
            90, -100, 110, -120,
           -10,  20,  -30,  40,
        };

        Span<int> coeff = stackalloc int[16];
        Span<int> dequant = stackalloc int[16];
        Span<int> reconstructed = stackalloc int[16];

        H264BlockTransform.ForwardDct4X4Scalar(residual, coeff);
        H264BlockTransform.Quant4X4Scalar(coeff, qp);
        H264BlockTransform.DequantAc4x4Spec(coeff, qp, dequant);
        H264BlockTransform.InverseDct4x4Spec(dequant, reconstructed);

        // After a spec-compliant roundtrip the per-pixel error is bounded by the
        // quantisation step projected back through the IDCT; ≤25 covers QP ∈ {22,28}.
        for (var i = 0; i < 16; i++)
        {
            Math.Abs(reconstructed[i] - (int)residual[i]).Should().BeLessThanOrEqualTo(25,
                $"pixel {i}: reconstructed {reconstructed[i]} should track original {(int)residual[i]} within quantisation error");
        }
    }
}
