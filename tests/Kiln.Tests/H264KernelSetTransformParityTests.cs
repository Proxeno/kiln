using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Smoke parity for <see cref="IH264KernelSet.EncodeResidual4x4"/> routing (scalar tier vs resolved SIMD tier).
/// Uses residual patterns covered by <see cref="H264TransformBundleSimdTests"/>; broader SIMD/scalar
/// bundle drift is tracked separately in that suite.
/// </summary>
public sealed class H264KernelSetTransformParityTests
{
    [Fact]
    public void EncodeResidual4x4_flat_residual_scalar_matches_create_best()
    {
        var best = H264KernelSet.CreateBest();
        if (best is ScalarKernelSet)
        {
            return;
        }

        Span<byte> src = stackalloc byte[16];
        Span<byte> pred = stackalloc byte[16];
        src.Fill(200);
        pred.Fill(200);

        Span<short> coeffScalar = stackalloc short[16];
        Span<short> coeffSimd = stackalloc short[16];
        Span<byte> recScalar = stackalloc byte[16];
        Span<byte> recSimd = stackalloc byte[16];

        const int qp = 28;
        var nzScalar = new ScalarKernelSet().EncodeResidual4x4(src, pred, qp, coeffScalar, recScalar, 4);
        var nzSimd = best.EncodeResidual4x4(src, pred, qp, coeffSimd, recSimd, 4);

        nzSimd.Should().Be(nzScalar);
        for (var i = 0; i < 16; i++)
        {
            coeffSimd[i].Should().Be(coeffScalar[i]);
            recSimd[i].Should().Be(recScalar[i]);
        }
    }

    [Fact]
    public void EncodeResidual4x4_kernel_set_matches_bundle_entry_points()
    {
        if (!H264TransformBundle.IsSimdBundleSupported)
        {
            return;
        }

        var rng = new Random(77);
        Span<byte> src = stackalloc byte[16];
        Span<byte> pred = stackalloc byte[16];
        Span<short> coeffScalar = stackalloc short[16];
        Span<short> coeffSimd = stackalloc short[16];
        Span<byte> recScalar = stackalloc byte[16];
        Span<byte> recSimd = stackalloc byte[16];

        rng.NextBytes(src);
        rng.NextBytes(pred);

        const int qp = 28;
        var nzScalar = new ScalarKernelSet().EncodeResidual4x4(src, pred, qp, coeffScalar, recScalar, 4);
        var nzSimd = H264KernelSet.CreateBest().EncodeResidual4x4(src, pred, qp, coeffSimd, recSimd, 4);
        var nzBundleScalar = H264TransformBundle.EncodeResidual4x4Scalar(src, pred, qp, coeffScalar, recScalar, 4);
        var nzBundleSimd = H264TransformBundle.EncodeResidual4x4Simd(src, pred, qp, coeffSimd, recSimd, 4);

        nzScalar.Should().Be(nzBundleScalar);
        nzSimd.Should().Be(nzBundleSimd);
    }
}
