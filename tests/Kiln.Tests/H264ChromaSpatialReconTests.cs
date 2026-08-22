using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

public sealed class H264ChromaSpatialReconTests
{
    [Fact]
    public void Encoder_chroma_dc_only_block_recon_mean_tracks_forward_dct_residual_mean()
    {
        Span<short> res = stackalloc short[16];
        Span<int> coeff = stackalloc int[16];
        for (var i = 0; i < 16; i++)
        {
            res[i] = -68;
        }

        H264BlockTransform.ForwardDct4X4Scalar(res, coeff);
        var dc = coeff[0];

        var qpc = H264ChromaDcScale.ChromaQpFromLuma(28, 0);
        var qmul = H264ChromaDcScale.ChromaDcQmul(qpc);
        Span<short> z = stackalloc short[4];
        Span<int> dc4 = stackalloc int[4];
        dc4[0] = dc4[1] = dc4[2] = dc4[3] = dc << 1;

        H264ChromaDcScale.QuantChromaDcLevelsFromDctDc(dc4, qmul, rdLambda: 0, z);
        H264ChromaDcScale.ChromaDcDequantIdct(z, qmul, dc4);

        Span<int> fwd = stackalloc int[16];
        Span<int> inv = stackalloc int[16];
        fwd.Clear();
        fwd[0] = dc4[0] << 1;
        H264BlockTransform.InverseDct4x4Spec(fwd, inv);

        var sum = 0;
        for (var i = 0; i < 16; i++)
        {
            sum += inv[i];
        }

        var mean = sum / 16.0;
        mean.Should().BeApproximately(-68.0, 12.0);
    }
}
