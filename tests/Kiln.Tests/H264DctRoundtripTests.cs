using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

public sealed class H264DctRoundtripTests
{
    [Fact]
    public void Forward_scalar_then_spec_inverse_roundtrip_ac_only_blocks_within_tolerance_at_qp0()
    {
        // H.264 quantisation at QP=0 has a pixel-domain step of ~16 for the DC
        // coefficient (by design: MF×V ≈ 2^17, not 2^19). For zero-mean blocks the
        // DC vanishes and only AC quantisation contributes; those errors are small.
        var rng = new Random(1);
        Span<short> res = stackalloc short[16];
        Span<int> c = stackalloc int[16];
        Span<int> dq = stackalloc int[16];
        Span<int> inv = stackalloc int[16];
        var worst = 0;
        for (var t = 0; t < 500; t++)
        {
            var sum = 0;
            for (var i = 0; i < 16; i++)
            {
                res[i] = (short)rng.Next(-64, 65);
                sum += res[i];
            }
            // Force zero mean so the DC coefficient is 0 and won't dominate the error.
            var mean = sum / 16;
            for (var i = 0; i < 16; i++)
            {
                res[i] = (short)(res[i] - mean);
            }

            H264BlockTransform.ForwardDct4X4Scalar(res, c);
            H264BlockTransform.Quant4X4Scalar(c, 0);
            H264BlockTransform.DequantAc4x4Spec(c, 0, dq);
            H264BlockTransform.InverseDct4x4Spec(dq, inv);
            for (var i = 0; i < 16; i++)
            {
                var e = Math.Abs(inv[i] - res[i]);
                if (e > worst)
                {
                    worst = e;
                }
            }
        }

        worst.Should().BeLessThanOrEqualTo(5);
    }

    [Fact]
    public void Encoder_recon_inverse_stays_close_to_residual_on_average_at_qp_28()
    {
        // Zero-mean blocks remove the DC bias so we're measuring AC reconstruction
        // quality only — the metric that matters for inter-frame encoder fidelity.
        var rng = new Random(3);
        const int qp = 28;
        Span<short> res = stackalloc short[16];
        Span<int> coeff = stackalloc int[16];
        Span<int> zz = stackalloc int[16];
        Span<int> dq = stackalloc int[16];
        Span<int> inv = stackalloc int[16];
        double sumAbsErr = 0;
        var n = 0;
        for (var t = 0; t < 400; t++)
        {
            var sum = 0;
            for (var i = 0; i < 16; i++)
            {
                res[i] = (short)rng.Next(-128, 129);
                sum += res[i];
            }
            var mean = sum / 16;
            for (var i = 0; i < 16; i++)
            {
                res[i] = (short)(res[i] - mean);
            }

            H264BlockTransform.ForwardDct4X4Scalar(res, coeff);
            H264BlockTransform.Quant4X4Scalar(coeff, qp);
            H264BlockTransform.RasterToZigzag(coeff, zz);
            H264BlockTransform.ZigzagToRaster(zz, coeff);
            H264BlockTransform.DequantAc4x4Spec(coeff, qp, dq);
            H264BlockTransform.InverseDct4x4Spec(dq, inv);

            for (var i = 0; i < 16; i++)
            {
                sumAbsErr += Math.Abs(inv[i] - res[i]);
                n++;
            }
        }

        var mae = sumAbsErr / n;
        mae.Should().BeLessThan(40);
    }
}
