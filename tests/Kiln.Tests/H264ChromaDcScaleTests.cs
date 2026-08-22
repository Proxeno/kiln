using FluentAssertions;
using Kiln.Internal.H264;

namespace Kiln.Tests;

public class H264ChromaDcScaleTests
{
    [Fact]
    public void Split_uv_frame_first_mb_chroma_dct_dc_values_are_non_trivial()
    {
        const int w = 32;
        const int h = 32;
        var uvW = w / 2;
        var u = new byte[w * h / 4];
        for (var r = 0; r < h / 2; r++)
        {
            for (var c = 0; c < uvW; c++)
            {
                u[r * uvW + c] = c < 8 ? (byte)40 : (byte)220;
            }
        }

        const byte pred = 128;
        Span<short> residualS = stackalloc short[16];
        Span<int> coeff = stackalloc int[16];
        Span<int> dc4 = stackalloc int[4];
        for (var blk = 0; blk < 4; blk++)
        {
            var ox = (blk & 1) * 4;
            var oy = (blk >> 1) * 4;
            for (var i = 0; i < 16; i++)
            {
                var r = oy + i / 4;
                var cc = ox + (i % 4);
                residualS[i] = (short)(u[r * uvW + cc] - pred);
            }

            H264BlockTransform.ForwardDct4X4(residualS, coeff);
            dc4[blk] = coeff[0];
        }

        Array.Exists(dc4.ToArray(), v => Math.Abs(v) > 50).Should().BeTrue(
            $"expected strong chroma split to yield |DCT DC| > 50, got {string.Join(",", dc4.ToArray())}");
    }

    [Fact]
    public void Chroma_dc_quant_dequant_is_sse_optimal_against_target_dct_at_qp_28_when_lambda_zero()
    {
        ReadOnlySpan<int> dct = [400, -120, 90, 0];
        var qpc = H264ChromaDcScale.ChromaQpFromLuma(28, 0);
        var qmul = H264ChromaDcScale.ChromaDcQmul(qpc);
        qmul.Should().BeGreaterThan(0);

        Span<short> z = stackalloc short[4];
        // Distortion-only (λ=0): this test measures DCT-domain round-trip error, not RD trade-offs.
        H264ChromaDcScale.QuantChromaDcLevelsFromDctDc(dct, qmul, rdLambda: 0, z);
        Array.Exists(z.ToArray(), v => v != 0).Should().BeTrue();

        Span<int> back = stackalloc int[4];
        H264ChromaDcScale.ChromaDcDequantIdct(z, qmul, back);
        // λ=0 minimizes SSE in DCT-domain DCs (see DefaultChromaDcRdCost), not L1; Z=(0,1,0,0) beats (0,1,0,1) on SSE here.
        double sse = 0;
        for (var i = 0; i < 4; i++)
        {
            var d = back[i] - dct[i];
            sse += d * d;
        }

        sse.Should().BeLessThanOrEqualTo(132324.0, $"Z=[{z[0]},{z[1]},{z[2]},{z[3]}] back=[{string.Join(",", back.ToArray())}]");
    }

    [Fact]
    public void Uniform_chroma_dc4_round_trip_matches_spec_dequant_at_qp_28()
    {
        Span<int> dc4 = [-2176, -2176, -2176, -2176];
        var qpc = H264ChromaDcScale.ChromaQpFromLuma(28, 0);
        var qmul = H264ChromaDcScale.ChromaDcQmul(qpc);
        qmul.Should().Be(16384,
            "H.264 §8.5.9 chroma-DC dequant at chromaQp=28: normAdjust4x4(qP%6,(0,0))=16, scaled by 2^(qP/6+2)");

        Span<short> z = stackalloc short[4];
        H264ChromaDcScale.QuantChromaDcLevelsFromDctDc(dc4, qmul, rdLambda: 0, z);
        z[0].Should().Be(-34, "scale = 64/qmul accounts for our 2× forward DCT vs spec");

        Span<int> back = stackalloc int[4];
        H264ChromaDcScale.ChromaDcDequantIdct(z, qmul, back);
        for (var i = 0; i < 4; i++)
        {
            Math.Abs(back[i] - dc4[i]).Should().BeLessThanOrEqualTo(12, $"back[{i}]={back[i]} dc4={dc4[i]}");
        }
    }
}
