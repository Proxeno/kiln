using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Phase 1 senior parity test gating Junior J3. Asserts that the (yet-to-land) SIMD inverse-quant
/// helper <see cref="H264BlockTransformDequantSimd.DequantApprox"/> produces lane-identical output
/// to the scalar reference <see cref="H264BlockTransform.DequantApprox"/> for every QP in [0, 51]
/// across an RNG sweep plus saturating-lane edge fixtures. This file is intentionally compile-failing
/// on <c>main</c>; J3's mandatory pre-check (rule 11) requires the test class to exist before they
/// implement <c>H264BlockTransformDequantSimd</c>. Skips on non-SIMD hosts.
/// </summary>
public sealed class H264DequantApproxSimdTests
{
    public static IEnumerable<object[]> AllQps()
    {
        for (var qp = 0; qp <= 51; qp++)
        {
            yield return [qp];
        }
    }

    private static void AssertParity(ReadOnlySpan<int> input, int qp)
    {
        Span<int> scalarOut = stackalloc int[16];
        Span<int> simdOut = stackalloc int[16];
        var scalarInput = new int[16];
        input.CopyTo(scalarInput);
        var simdInput = new int[16];
        input.CopyTo(simdInput);

        Exception? scalarException = null;
        try
        {
            H264BlockTransform.DequantApprox(scalarInput, qp, scalarOut);
        }
        catch (Exception ex)
        {
            scalarException = ex;
        }

        Exception? simdException = null;
        try
        {
            H264BlockTransformDequantSimd.DequantApprox(simdInput, qp, simdOut);
        }
        catch (Exception ex)
        {
            simdException = ex;
        }

        if (scalarException is not null)
        {
            simdException.Should().NotBeNull(
                $"scalar threw {scalarException.GetType().Name} for qp={qp}; SIMD must mirror that contract");
            return;
        }

        simdException.Should().BeNull($"scalar succeeded but SIMD threw for qp={qp}");
        for (var i = 0; i < 16; i++)
        {
            simdOut[i].Should().Be(scalarOut[i], $"qp={qp} pos={i}");
        }
    }

    [Theory]
    [MemberData(nameof(AllQps))]
    public void DequantApprox_simd_matches_scalar_for_rng_sweep(int qp)
    {
        if (!H264BlockTransformDequantSimd.IsSupported)
        {
            return;
        }

        var rng = new Random(qp * 31 + 7);
        Span<int> input = stackalloc int[16];
        for (var trial = 0; trial < 50; trial++)
        {
            for (var i = 0; i < 16; i++)
            {
                input[i] = rng.Next(-2000, 2000);
            }

            AssertParity(input, qp);
        }
    }

    [Theory]
    [MemberData(nameof(AllQps))]
    public void DequantApprox_simd_matches_scalar_for_all_zero(int qp)
    {
        if (!H264BlockTransformDequantSimd.IsSupported)
        {
            return;
        }

        Span<int> input = stackalloc int[16];
        AssertParity(input, qp);
    }

    [Theory]
    [MemberData(nameof(AllQps))]
    public void DequantApprox_simd_matches_scalar_for_single_non_zero_at_each_position(int qp)
    {
        if (!H264BlockTransformDequantSimd.IsSupported)
        {
            return;
        }

        Span<int> input = stackalloc int[16];
        for (var pos = 0; pos < 16; pos++)
        {
            input.Clear();
            input[pos] = 1234;
            AssertParity(input, qp);

            input.Clear();
            input[pos] = -1234;
            AssertParity(input, qp);
        }
    }

    /// <summary>
    /// Saturating single-lane fixtures. Scalar <see cref="H264BlockTransform.DequantApprox"/> calls
    /// <see cref="Math.Abs(int)"/> which throws <see cref="OverflowException"/> for
    /// <see cref="int.MinValue"/>; the SIMD path must mirror whatever the scalar does (throw or
    /// produce the same wrapped value). <see cref="int.MaxValue"/> stays inside <see cref="Math.Abs"/>'s
    /// domain but the subsequent <c>(abs &lt;&lt; qbits) / mf</c> wraps; both paths must wrap identically.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllQps))]
    public void DequantApprox_simd_matches_scalar_for_int_min_value_lane_zero(int qp)
    {
        if (!H264BlockTransformDequantSimd.IsSupported)
        {
            return;
        }

        Span<int> input = stackalloc int[16];
        input[0] = int.MinValue;
        AssertParity(input, qp);
    }

    [Theory]
    [MemberData(nameof(AllQps))]
    public void DequantApprox_simd_matches_scalar_for_int_max_value_lane_zero(int qp)
    {
        if (!H264BlockTransformDequantSimd.IsSupported)
        {
            return;
        }

        Span<int> input = stackalloc int[16];
        input[0] = int.MaxValue;
        AssertParity(input, qp);
    }
}
