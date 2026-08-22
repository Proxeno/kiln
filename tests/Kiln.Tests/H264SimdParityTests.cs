using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Phase 1 senior parity tests guarding the SIMD/scalar tear-downs in
/// <see cref="H264BlockTransformSimd"/>, <see cref="H264Intra4X4Simd"/>, and
/// <see cref="H264Dct4x4Simd"/>. The DCT/IDCT cases were tightened in Phase 1 to gate Junior J2:
/// trial counts bumped to ≥500 plus explicit edge fixtures (all-zero, saturating residuals,
/// alternating sign, single non-zero per position, single-lane <see cref="int.MinValue"/> /
/// <see cref="int.MaxValue"/> on the IDCT). The Quant and SAD cases are left intact as a safety net
/// alongside the dedicated <see cref="H264SadHorizontalSumTests"/> and
/// <c>H264DequantApproxSimdTests</c> coverage.
/// </summary>
public sealed class H264SimdParityTests
{
    [Fact]
    public void Quant4X4_simd_matches_scalar_when_intrinsics_available()
    {
        if (!H264BlockTransformSimd.IsSupported)
        {
            return;
        }

        var rng = new Random(42);
        Span<int> a = stackalloc int[16];
        Span<int> b = stackalloc int[16];
        Span<int> s = stackalloc int[16];
        for (var trial = 0; trial < 50; trial++)
        {
            for (var i = 0; i < 16; i++)
            {
                a[i] = rng.Next(-2000, 2000);
            }

            var qp = rng.Next(0, 52);
            a.CopyTo(b);
            a.CopyTo(s);
            H264BlockTransformSimd.Quant4X4(b, qp);
            H264BlockTransform.Quant4X4Scalar(s, qp);
            for (var i = 0; i < 16; i++)
            {
                b[i].Should().Be(s[i]);
            }
        }
    }

    [Fact]
    public void SadU8x16_simd_matches_scalar_when_available()
    {
        if (!H264Intra4X4Simd.IsSupported)
        {
            return;
        }

        var rng = new Random(7);
        var a = new byte[16];
        var b = new byte[16];
        rng.NextBytes(a);
        rng.NextBytes(b);
        var simd = H264Intra4X4Simd.SadU8x16(a, b);
        var sc = 0;
        for (var i = 0; i < 16; i++)
        {
            sc += Math.Abs(a[i] - b[i]);
        }

        simd.Should().Be(sc);
    }

    [Fact]
    public void ForwardDct4X4_simd_matches_scalar_when_available()
    {
        if (!H264Dct4x4Simd.IsSupported)
        {
            return;
        }

        var rng = new Random(99);
        Span<short> residual = stackalloc short[16];
        Span<int> a = stackalloc int[16];
        Span<int> b = stackalloc int[16];
        for (var trial = 0; trial < 500; trial++)
        {
            for (var i = 0; i < 16; i++)
            {
                residual[i] = (short)rng.Next(-4000, 4001);
            }

            H264BlockTransform.ForwardDct4X4Scalar(residual, a);
            H264Dct4x4Simd.ForwardDct4X4(residual, b);
            for (var i = 0; i < 16; i++)
            {
                b[i].Should().Be(a[i], $"trial={trial} pos={i}");
            }
        }
    }

    [Fact]
    public void ForwardDct4X4_simd_matches_scalar_for_all_zero_residual()
    {
        if (!H264Dct4x4Simd.IsSupported)
        {
            return;
        }

        Span<short> residual = stackalloc short[16];
        Span<int> a = stackalloc int[16];
        Span<int> b = stackalloc int[16];
        H264BlockTransform.ForwardDct4X4Scalar(residual, a);
        H264Dct4x4Simd.ForwardDct4X4(residual, b);
        for (var i = 0; i < 16; i++)
        {
            b[i].Should().Be(a[i], $"pos={i}");
        }
    }

    [Fact]
    public void ForwardDct4X4_simd_matches_scalar_for_saturated_residuals()
    {
        if (!H264Dct4x4Simd.IsSupported)
        {
            return;
        }

        Span<short> residual = stackalloc short[16];
        Span<int> a = stackalloc int[16];
        Span<int> b = stackalloc int[16];

        residual.Fill(short.MinValue);
        H264BlockTransform.ForwardDct4X4Scalar(residual, a);
        H264Dct4x4Simd.ForwardDct4X4(residual, b);
        for (var i = 0; i < 16; i++)
        {
            b[i].Should().Be(a[i], $"all short.MinValue pos={i}");
        }

        residual.Fill(short.MaxValue);
        H264BlockTransform.ForwardDct4X4Scalar(residual, a);
        H264Dct4x4Simd.ForwardDct4X4(residual, b);
        for (var i = 0; i < 16; i++)
        {
            b[i].Should().Be(a[i], $"all short.MaxValue pos={i}");
        }
    }

    [Fact]
    public void ForwardDct4X4_simd_matches_scalar_for_alternating_sign_residual()
    {
        if (!H264Dct4x4Simd.IsSupported)
        {
            return;
        }

        Span<short> residual = stackalloc short[16];
        Span<int> a = stackalloc int[16];
        Span<int> b = stackalloc int[16];
        for (var i = 0; i < 16; i++)
        {
            residual[i] = (i & 1) == 0 ? (short)4000 : (short)-4000;
        }

        H264BlockTransform.ForwardDct4X4Scalar(residual, a);
        H264Dct4x4Simd.ForwardDct4X4(residual, b);
        for (var i = 0; i < 16; i++)
        {
            b[i].Should().Be(a[i], $"alternating pos={i}");
        }
    }

    [Fact]
    public void ForwardDct4X4_simd_matches_scalar_for_single_non_zero_at_each_position()
    {
        if (!H264Dct4x4Simd.IsSupported)
        {
            return;
        }

        Span<short> residual = stackalloc short[16];
        Span<int> a = stackalloc int[16];
        Span<int> b = stackalloc int[16];
        for (var pos = 0; pos < 16; pos++)
        {
            residual.Clear();
            residual[pos] = 1234;
            H264BlockTransform.ForwardDct4X4Scalar(residual, a);
            H264Dct4x4Simd.ForwardDct4X4(residual, b);
            for (var i = 0; i < 16; i++)
            {
                b[i].Should().Be(a[i], $"single non-zero at pos={pos} read i={i}");
            }
        }
    }

    [Fact]
    public void InverseDctMatrixMultiply_simd_matches_scalar_when_available()
    {
        if (!H264Dct4x4Simd.IsSupported)
        {
            return;
        }

        var rng = new Random(101);
        Span<int> fwd = stackalloc int[16];
        Span<int> a = stackalloc int[16];
        Span<int> b = stackalloc int[16];
        for (var trial = 0; trial < 500; trial++)
        {
            for (var i = 0; i < 16; i++)
            {
                fwd[i] = rng.Next(-500_000, 500_001);
            }

            H264BlockTransform.InverseDctMatrixMultiplyScalar(fwd, a);
            H264Dct4x4Simd.InverseDctMatrixMultiply(H264BlockTransform.InverseDctMatrixCoefficientsInt32, fwd, b);
            for (var i = 0; i < 16; i++)
            {
                b[i].Should().Be(a[i], $"trial={trial} pos={i}");
            }
        }
    }

    [Fact]
    public void InverseDctMatrixMultiply_simd_matches_scalar_for_all_zero_input()
    {
        if (!H264Dct4x4Simd.IsSupported)
        {
            return;
        }

        Span<int> fwd = stackalloc int[16];
        Span<int> a = stackalloc int[16];
        Span<int> b = stackalloc int[16];
        H264BlockTransform.InverseDctMatrixMultiplyScalar(fwd, a);
        H264Dct4x4Simd.InverseDctMatrixMultiply(H264BlockTransform.InverseDctMatrixCoefficientsInt32, fwd, b);
        for (var i = 0; i < 16; i++)
        {
            b[i].Should().Be(a[i], $"pos={i}");
        }
    }

    [Fact]
    public void InverseDctMatrixMultiply_simd_matches_scalar_for_alternating_sign_input()
    {
        if (!H264Dct4x4Simd.IsSupported)
        {
            return;
        }

        Span<int> fwd = stackalloc int[16];
        Span<int> a = stackalloc int[16];
        Span<int> b = stackalloc int[16];
        for (var i = 0; i < 16; i++)
        {
            fwd[i] = (i & 1) == 0 ? 500_000 : -500_000;
        }

        H264BlockTransform.InverseDctMatrixMultiplyScalar(fwd, a);
        H264Dct4x4Simd.InverseDctMatrixMultiply(H264BlockTransform.InverseDctMatrixCoefficientsInt32, fwd, b);
        for (var i = 0; i < 16; i++)
        {
            b[i].Should().Be(a[i], $"alternating pos={i}");
        }
    }

    [Fact]
    public void InverseDctMatrixMultiply_simd_matches_scalar_for_single_non_zero_at_each_position()
    {
        if (!H264Dct4x4Simd.IsSupported)
        {
            return;
        }

        Span<int> fwd = stackalloc int[16];
        Span<int> a = stackalloc int[16];
        Span<int> b = stackalloc int[16];
        for (var pos = 0; pos < 16; pos++)
        {
            fwd.Clear();
            fwd[pos] = 500_000;
            H264BlockTransform.InverseDctMatrixMultiplyScalar(fwd, a);
            H264Dct4x4Simd.InverseDctMatrixMultiply(H264BlockTransform.InverseDctMatrixCoefficientsInt32, fwd, b);
            for (var i = 0; i < 16; i++)
            {
                b[i].Should().Be(a[i], $"single non-zero at pos={pos} read i={i}");
            }
        }
    }

    /// <summary>
    /// Single-lane <see cref="int.MinValue"/> / <see cref="int.MaxValue"/> / 0 fixtures. With the
    /// inverse DCT matrix coefficients bounded by ±16, a single saturating lane yields per-row sums
    /// well within <see cref="long"/> range; the resulting <c>(int)(sum / 1024)</c> stays within
    /// <see cref="int"/>. Any divergence between scalar and SIMD wrap-around behaviour shows up here
    /// before it can leak into the encoder bitstream.
    /// </summary>
    [Fact]
    public void InverseDctMatrixMultiply_simd_matches_scalar_for_int_extreme_lanes()
    {
        if (!H264Dct4x4Simd.IsSupported)
        {
            return;
        }

        Span<int> fwd = stackalloc int[16];
        Span<int> a = stackalloc int[16];
        Span<int> b = stackalloc int[16];

        for (var pos = 0; pos < 16; pos++)
        {
            fwd.Clear();
            fwd[pos] = int.MinValue;
            H264BlockTransform.InverseDctMatrixMultiplyScalar(fwd, a);
            H264Dct4x4Simd.InverseDctMatrixMultiply(H264BlockTransform.InverseDctMatrixCoefficientsInt32, fwd, b);
            for (var i = 0; i < 16; i++)
            {
                b[i].Should().Be(a[i], $"int.MinValue at pos={pos} read i={i}");
            }
        }

        for (var pos = 0; pos < 16; pos++)
        {
            fwd.Clear();
            fwd[pos] = int.MaxValue;
            H264BlockTransform.InverseDctMatrixMultiplyScalar(fwd, a);
            H264Dct4x4Simd.InverseDctMatrixMultiply(H264BlockTransform.InverseDctMatrixCoefficientsInt32, fwd, b);
            for (var i = 0; i < 16; i++)
            {
                b[i].Should().Be(a[i], $"int.MaxValue at pos={pos} read i={i}");
            }
        }
    }
}
