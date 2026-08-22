// Parity tests for the vectorized dequant+IDCT reconstruction tail (B1).
//
// Guard: these tests compile and run only when HAS_SIMD_RECON is defined, which signals that
// H264TransformBundle.DequantIdct4x4Simd has been implemented. Until then the placeholder
// test fires and fails loudly, reminding the developer to implement the feature first.

using Xunit;

#if HAS_SIMD_RECON
using FluentAssertions;
using Kiln.Internal.H264;
#endif

namespace Kiln.Tests;

/// <summary>
/// Parity tests verifying that the SIMD dequant+IDCT reconstruction tail
/// (<see cref="H264TransformBundle.DequantIdct4x4Simd"/>) is byte-exact with the scalar
/// <see cref="H264BlockTransform.DequantAc4x4Spec"/> + <see cref="H264BlockTransform.InverseDct4x4Spec"/>
/// path for every QP (0..51) and a representative set of random quantised coefficient blocks.
///
/// A second group of tests re-verifies that <see cref="H264TransformBundle.EncodeResidual4x4Simd"/>
/// remains bit-exact with <see cref="H264TransformBundle.EncodeResidual4x4Scalar"/> after the
/// SIMD tail is wired in.
/// </summary>
public sealed class H264DequantIdctSimdParityTests
{
#if !HAS_SIMD_RECON
    [Fact]
    public void DequantIdctSimd_must_be_delivered_before_tests_run()
    {
        var t = Type.GetType("Kiln.Internal.H264.H264TransformBundle, Kiln");
        if (t is null) { Assert.Fail("H264TransformBundle not found in Kiln assembly."); return; }
        var m = t.GetMethod("DequantIdct4x4Simd",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (m is null)
        {
            Assert.Fail(
                "H264TransformBundle.DequantIdct4x4Simd is missing. " +
                "Implement the vectorized dequant+IDCT tail per the B1 plan step, " +
                "then enable HAS_SIMD_RECON in Kiln.Tests.csproj.");
        }

        Assert.Fail("HAS_SIMD_RECON must be set in Kiln.Tests.csproj.");
    }
#else
    // ── DequantIdct4x4Simd parity ─────────────────────────────────────────────

    /// <summary>
    /// For every QP 0..51 and 200 random quantised coefficient blocks, the SIMD
    /// dequant+IDCT reconstruction output is byte-exact with the scalar reference.
    /// Only runs when the ISA supports the SIMD path (SSE4.1 or AdvSimd).
    /// </summary>
    [Fact]
    public void DequantIdct4x4Simd_matches_scalar_for_all_qp_and_random_blocks()
    {
        if (!H264TransformBundle.IsSimdBundleSupported) return;

        var rng = new Random(0xB1_0001);
        Span<int> qRaster = stackalloc int[16];
        Span<byte> pred = stackalloc byte[16];
        Span<byte> reconSimd = stackalloc byte[16];
        Span<byte> reconScalar = stackalloc byte[16];

        Span<int> dq = stackalloc int[16];
        Span<int> invRes = stackalloc int[16];

        for (var qp = 0; qp <= 51; qp++)
        {
            for (var trial = 0; trial < 200; trial++)
            {
                // Random quantised coefficients in a plausible range for the given QP.
                for (var i = 0; i < 16; i++)
                    qRaster[i] = rng.Next(-64, 65);
                rng.NextBytes(pred);

                // Scalar reference: dequant → IDCT → reconstruct.
                H264BlockTransform.DequantAc4x4Spec(qRaster, qp, dq);
                H264BlockTransform.InverseDct4x4Spec(dq, invRes);
                for (var y = 0; y < 4; y++)
                for (var x = 0; x < 4; x++)
                    reconScalar[y * 4 + x] = (byte)Math.Clamp(pred[y * 4 + x] + invRes[y * 4 + x], 0, 255);

                // SIMD path under test.
                H264TransformBundle.DequantIdct4x4Simd(qRaster, qp, pred, reconSimd, recStride: 4);

                for (var i = 0; i < 16; i++)
                    reconSimd[i].Should().Be(reconScalar[i],
                        $"qp={qp} trial={trial} pos={i}");
            }
        }
    }

    /// <summary>
    /// Degenerate case: all-zero quantised coefficients. SIMD must reconstruct
    /// identically to the prediction (no residual), matching scalar.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(28)]
    [InlineData(51)]
    public void DequantIdct4x4Simd_all_zero_coeffs_yields_prediction(int qp)
    {
        if (!H264TransformBundle.IsSimdBundleSupported) return;

        Span<int> qRaster = stackalloc int[16];
        qRaster.Clear();
        Span<byte> pred = new byte[] {
            100, 110, 120, 130,
            105, 115, 125, 135,
            110, 120, 130, 140,
            115, 125, 135, 145,
        };
        Span<byte> recSimd = stackalloc byte[16];
        H264TransformBundle.DequantIdct4x4Simd(qRaster, qp, pred, recSimd, recStride: 4);

        for (var i = 0; i < 16; i++)
            recSimd[i].Should().Be(pred[i], $"pos={i}: zero residual must leave pred unchanged");
    }

    // ── EncodeResidual4x4Simd full-pipeline parity (all QPs) ─────────────────

    /// <summary>
    /// After the SIMD tail is wired in, <see cref="H264TransformBundle.EncodeResidual4x4Simd"/>
    /// must remain bit-exact with the scalar bundle for all 52 QPs and random inputs.
    /// Extends the coverage in <see cref="H264TransformBundleSimdTests"/> to every QP.
    /// </summary>
    [Fact]
    public void EncodeResidual4x4Simd_matches_scalar_for_all_qp()
    {
        if (!H264TransformBundle.IsSimdBundleSupported) return;

        var rng = new Random(0xB1_0002);
        Span<byte> src = stackalloc byte[16];
        Span<byte> pred = stackalloc byte[16];
        Span<short> zzSimd = stackalloc short[16];
        Span<short> zzScalar = stackalloc short[16];
        Span<byte> reconSimd = stackalloc byte[16];
        Span<byte> reconScalar = stackalloc byte[16];

        for (var qp = 0; qp <= 51; qp++)
        {
            for (var trial = 0; trial < 100; trial++)
            {
                rng.NextBytes(src);
                rng.NextBytes(pred);

                var nzS = H264TransformBundle.EncodeResidual4x4Scalar(src, pred, qp, zzScalar, reconScalar, recStride: 4);
                var nzV = H264TransformBundle.EncodeResidual4x4Simd(src, pred, qp, zzSimd, reconSimd, recStride: 4);

                nzV.Should().Be(nzS, $"qp={qp} trial={trial}: nz mismatch");
                for (var i = 0; i < 16; i++)
                {
                    zzSimd[i].Should().Be(zzScalar[i], $"qp={qp} trial={trial} zz[{i}] mismatch");
                    reconSimd[i].Should().Be(reconScalar[i], $"qp={qp} trial={trial} recon[{i}] mismatch");
                }
            }
        }
    }
#endif
}
