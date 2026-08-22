using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

public sealed class H264TransformBundleSimdTests
{
    [Fact]
    public void EncodeResidual4x4Simd_matches_scalar_when_bundle_isa_supported()
    {
        if (!H264TransformBundle.IsSimdBundleSupported)
        {
            return;
        }

        var rng = new Random(202);
        Span<byte> src = stackalloc byte[16];
        Span<byte> pred = stackalloc byte[16];
        Span<short> zzSimd = stackalloc short[16];
        Span<short> zzScalar = stackalloc short[16];
        Span<byte> reconSimd = stackalloc byte[16];
        Span<byte> reconScalar = stackalloc byte[16];

        foreach (var qp in new[] { 0, 12, 28, 40, 51 })
        {
            for (var trial = 0; trial < 400; trial++)
            {
                rng.NextBytes(src);
                rng.NextBytes(pred);

                var nzS = H264TransformBundle.EncodeResidual4x4Scalar(src, pred, qp, zzScalar, reconScalar, recStride: 4);
                var nzV = H264TransformBundle.EncodeResidual4x4Simd(src, pred, qp, zzSimd, reconSimd, recStride: 4);

                nzV.Should().Be(nzS, $"trial={trial} qp={qp}");
                for (var i = 0; i < 16; i++)
                {
                    zzSimd[i].Should().Be(zzScalar[i], $"trial={trial} qp={qp} zz={i}");
                    reconSimd[i].Should().Be(reconScalar[i], $"trial={trial} qp={qp} recon={i}");
                }
            }
        }
    }

    [Fact]
    public void EncodeResidual4x4Simd_matches_scalar_all_equal_pixels()
    {
        if (!H264TransformBundle.IsSimdBundleSupported)
        {
            return;
        }

        Span<byte> src = stackalloc byte[16];
        Span<byte> pred = stackalloc byte[16];
        Span<short> zzSimd = stackalloc short[16];
        Span<short> zzScalar = stackalloc short[16];
        Span<byte> reconSimd = stackalloc byte[16];
        Span<byte> reconScalar = stackalloc byte[16];

        src.Fill(200);
        pred.Fill(200);

        var nzS = H264TransformBundle.EncodeResidual4x4Scalar(src, pred, 28, zzScalar, reconScalar, recStride: 4);
        var nzV = H264TransformBundle.EncodeResidual4x4Simd(src, pred, 28, zzSimd, reconSimd, recStride: 4);

        nzV.Should().Be(nzS);
        reconSimd.ToArray().Should().Equal(reconScalar.ToArray());
        zzSimd.ToArray().Should().Equal(zzScalar.ToArray());
    }
}
