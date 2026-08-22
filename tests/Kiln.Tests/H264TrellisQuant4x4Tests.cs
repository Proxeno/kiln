using System.Numerics;
using FluentAssertions;
using Kiln.Internal.H264;

namespace Kiln.Tests;

public sealed class H264TrellisQuant4x4Tests
{
    private static int ZigzagIndexForRaster(ReadOnlySpan<byte> zzRasterMap, byte rasterIdx)
    {
        for (var z = 0; z < 16; z++)
        {
            if (zzRasterMap[z] == rasterIdx)
            {
                return z;
            }
        }

        return -1;
    }

    [Fact]
    public void Apply_never_increases_coefficient_magnitude()
    {
        Span<short> residual = stackalloc short[16];
        residual.Fill(37);
        Span<int> coeff = stackalloc int[16];
        H264BlockTransform.ForwardDct4X4Scalar(residual, coeff);
        var preQuantSnap = coeff.ToArray();
        const int qp = 26;
        H264BlockTransform.Quant4X4Scalar(coeff, qp);

        var zz = H264Zigzag.Frame4X4;
        Span<short> zig = stackalloc short[16];
        for (var i = 0; i < 16; i++)
        {
            zig[i] = (short)coeff[zz[i]];
        }

        Span<int> forward = stackalloc int[16];
        preQuantSnap.AsSpan().CopyTo(forward);

        var nz = H264TrellisQuant4x4.Apply(zig, forward, qp, lambda2: 512);
        nz.Should().BeGreaterThanOrEqualTo(0);

        for (var i = 0; i < 16; i++)
        {
            var before = coeff[zz[i]];
            Math.Abs((int)zig[i]).Should().BeLessThanOrEqualTo(Math.Abs(before));
        }
    }

    [Fact]
    public void Apply_with_all_zero_forward_coefficients_produces_all_zero_irrespective_of_lambda()
    {
        Span<short> zig = stackalloc short[16];
        Span<int> forward = stackalloc int[16];
        foreach (var lambda2 in new[] { 0, 1, 4096 })
        {
            for (var i = 0; i < 16; i++)
            {
                zig[i] = -3;
            }

            H264TrellisQuant4x4.Apply(zig, forward, qp: 28, lambda2).Should().Be(0);
            zig.ToArray().Should().OnlyContain(v => v == 0);
        }
    }

    /// <summary>λ²=0 minimizes D only; λ² huge heavily penalizes any non-zero coefficient bits.</summary>
    [Fact]
    public void Lambda_extremes_shift_preference_between_nonzero_and_zero_for_high_frequency_pulse()
    {
        const int rasterPulse = 15;
        const int qp = 28;
        var zz = H264Zigzag.Frame4X4;
        var zigIdxPulse = ZigzagIndexForRaster(zz, rasterPulse);
        zigIdxPulse.Should().BeGreaterThanOrEqualTo(0);

        Span<int> work = stackalloc int[16];
        Span<int> forward = stackalloc int[16];
        var amplitude = -200;
        for (; amplitude > -2_000_000; amplitude -= 137)
        {
            work.Fill(0);
            work[rasterPulse] = amplitude;
            work.CopyTo(forward);
            H264BlockTransform.Quant4X4Scalar(work, qp);
            if (work[rasterPulse] != 0)
            {
                break;
            }
        }

        work[rasterPulse].Should().NotBe(0, "sanity: spectral impulse amplitude must survive quant at this QP");

        Span<short> zLo = stackalloc short[16];
        for (var i = 0; i < 16; i++)
        {
            zLo[i] = (short)work[zz[i]];
        }

        Span<int> fwdLo = stackalloc int[16];
        forward.CopyTo(fwdLo);
        H264TrellisQuant4x4.Apply(zLo, fwdLo, qp, lambda2: 0);
        zLo[zigIdxPulse].Should().NotBe(0);

        Span<int> coeffQuantRestart = stackalloc int[16];
        forward.CopyTo(coeffQuantRestart);
        H264BlockTransform.Quant4X4Scalar(coeffQuantRestart, qp);
        coeffQuantRestart[rasterPulse].Should().NotBe(0);

        Span<short> zHi = stackalloc short[16];
        for (var i = 0; i < 16; i++)
        {
            zHi[i] = (short)coeffQuantRestart[zz[i]];
        }

        Span<int> fwdHi = stackalloc int[16];
        forward.CopyTo(fwdHi);
        H264TrellisQuant4x4.Apply(zHi, fwdHi, qp, lambda2: 1_000_000);
        zHi[zigIdxPulse].Should().Be(0);
    }

    [Fact]
    public void EncodeResidual4x4Trellis_matches_spec_dequant_idct_reconstruction()
    {
        ReadOnlySpan<byte> src =
        [
            200, 44, 12, 200,
            10,  90, 200, 3,
            33,   9,   9, 9,
            22,   9,   80, 2,
        ];
        ReadOnlySpan<byte> pred =
        [
            128, 128, 128, 128,
            128, 128, 128, 128,
            128, 128, 128, 128,
            128, 128, 128, 128,
        ];
        var qp = 30;
        var lambda = H264TrellisQuant4x4.LambdaForQp(qp);

        Span<short> zz = stackalloc short[16];
        Span<byte> recon = stackalloc byte[16];
        var nz = H264TransformBundle.EncodeResidual4x4Trellis(src, pred, qp, zz, recon, recStride: 4, lambda);
        nz.Should().BeGreaterThanOrEqualTo(0);

        Span<int> zzInt = stackalloc int[16];
        for (var i = 0; i < 16; i++)
        {
            zzInt[i] = zz[i];
        }

        Span<int> qr = stackalloc int[16];
        H264BlockTransform.ZigzagToRaster(zzInt, qr);
        Span<int> dq = stackalloc int[16];
        H264BlockTransform.DequantAc4x4Spec(qr, qp, dq);
        Span<int> idct = stackalloc int[16];
        H264BlockTransform.InverseDct4x4Spec(dq, idct);

        for (var rr = 0; rr < 4; rr++)
        {
            for (var c = 0; c < 4; c++)
            {
                var i = rr * 4 + c;
                recon[rr * 4 + c].Should().Be(
                    (byte)Math.Clamp(pred[i] + idct[i], 0, 255));
            }
        }
    }

    [Fact]
    public void ApproxRateBits_follows_piecewise_approximation()
    {
        H264TrellisQuant4x4.ApproxRateBits(0).Should().Be(0);
        H264TrellisQuant4x4.ApproxRateBits(1).Should().Be(3);
        H264TrellisQuant4x4.ApproxRateBits(-1).Should().Be(3);
        H264TrellisQuant4x4.ApproxRateBits(2).Should().Be(4 + BitOperations.Log2(1u) + 1);
        H264TrellisQuant4x4.ApproxRateBits(-100).Should().Be(4 + BitOperations.Log2(99u) + 1);
    }
}
