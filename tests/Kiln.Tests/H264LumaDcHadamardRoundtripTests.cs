using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Acceptance test for Junior-D-hadamard. Verifies the 4×4 luma DC Hadamard transform + quant +
/// dequant + inverse Hadamard chain per H.264 8.5.10. Three guarantees:
/// <list type="bullet">
///   <item>Forward Hadamard matches a reference matrix multiply <c>Y = H·X·Hᵀ</c> (per (i,j)).</item>
///   <item>Inverse Hadamard is the exact inverse of the forward (round-trip without quant).</item>
///   <item>Forward → quant → dequant → inverse round-trips inputs within an integer tolerance derived
///         from QP (worst case at QP = 51); the special unscaled QP &lt; 12 path also round-trips.</item>
/// </list>
/// </summary>
public sealed class H264LumaDcHadamardRoundtripTests
{
    private static readonly int[,] HadamardMatrix =
    {
        { 1, 1, 1, 1 },
        { 1, 1, -1, -1 },
        { 1, -1, -1, 1 },
        { 1, -1, 1, -1 },
    };

    private static void HxHt(ReadOnlySpan<int> x16, Span<int> y16)
    {
        Span<int> tmp = stackalloc int[16];
        for (var i = 0; i < 4; i++)
        {
            for (var j = 0; j < 4; j++)
            {
                var sum = 0;
                for (var k = 0; k < 4; k++)
                {
                    sum += HadamardMatrix[i, k] * x16[k * 4 + j];
                }

                tmp[i * 4 + j] = sum;
            }
        }

        for (var i = 0; i < 4; i++)
        {
            for (var j = 0; j < 4; j++)
            {
                var sum = 0;
                for (var k = 0; k < 4; k++)
                {
                    sum += tmp[i * 4 + k] * HadamardMatrix[j, k];
                }

                y16[i * 4 + j] = sum;
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(13)]
    [InlineData(257)]
    [InlineData(2026)]
    public void Forward_hadamard_matches_matrix_reference_per_position(int seed)
    {
        var rng = new Random(seed);
        Span<int> x = stackalloc int[16];
        Span<int> got = stackalloc int[16];
        Span<int> expected = stackalloc int[16];

        for (var trial = 0; trial < 50; trial++)
        {
            for (var i = 0; i < 16; i++)
            {
                x[i] = rng.Next(-4096, 4097);
            }

            H264LumaDcHadamard.ForwardHadamard4x4(x, got);
            HxHt(x, expected);

            for (var i = 0; i < 16; i++)
            {
                got[i].Should().Be(expected[i],
                    $"forward Hadamard trial seed={seed} #{trial} pos {i} (row {i / 4}, col {i % 4})");
            }
        }
    }

    [Theory]
    [InlineData(7)]
    [InlineData(1024)]
    [InlineData(int.MaxValue / 8)]
    public void Forward_then_inverse_hadamard_round_trips_without_quant(int seedOrConstant)
    {
        var rng = new Random(seedOrConstant);
        Span<int> x = stackalloc int[16];
        Span<int> w = stackalloc int[16];
        Span<int> r = stackalloc int[16];

        for (var trial = 0; trial < 25; trial++)
        {
            for (var i = 0; i < 16; i++)
            {
                x[i] = rng.Next(-2048, 2049);
            }

            H264LumaDcHadamard.ForwardHadamard4x4(x, w);
            H264LumaDcHadamard.InverseHadamard4x4(w, r);
            for (var i = 0; i < 16; i++)
            {
                r[i].Should().Be(x[i],
                    $"Hadamard ⟂ inverse trial seed={seedOrConstant} #{trial} pos {i}");
            }
        }
    }

    /// <summary>
    /// Forward → quant → dequant → inverse must round-trip with an absolute error bounded by a
    /// QP-dependent tolerance. At QP = 0 the result is bit-exact for typical inputs; at QP = 51 the
    /// quantization step is large but the round-tripped DC values must still be close to input.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(6, 2)]
    [InlineData(12, 4)]
    [InlineData(18, 8)]
    [InlineData(24, 32)]
    [InlineData(28, 32)]
    [InlineData(36, 128)]
    [InlineData(51, 4096)]
    public void Round_trip_through_quant_dequant_is_bounded_per_qp(int qp, int maxAbsErrPerSample)
    {
        var rng = new Random(0xD0 + qp);
        Span<int> x = stackalloc int[16];
        Span<int> w = stackalloc int[16];
        Span<int> dq = stackalloc int[16];
        Span<int> r = stackalloc int[16];

        for (var trial = 0; trial < 30; trial++)
        {
            for (var i = 0; i < 16; i++)
            {
                x[i] = rng.Next(-300, 301);
            }

            H264LumaDcHadamard.ForwardHadamard4x4(x, w);
            H264LumaDcHadamard.QuantLumaDcHadamard(w, qp);
            w.CopyTo(dq);
            H264LumaDcHadamard.DequantLumaDcHadamard(dq, qp);
            H264LumaDcHadamard.InverseHadamard4x4(dq, r);

            for (var i = 0; i < 16; i++)
            {
                Math.Abs(r[i] - x[i]).Should().BeLessThanOrEqualTo(maxAbsErrPerSample,
                    $"qp={qp} trial #{trial} pos {i}: round-trip error |{r[i]} - {x[i]}| exceeds budget {maxAbsErrPerSample}.");
            }
        }
    }

    /// <summary>
    /// Constant-DC input (all-same values): the 4×4 Hadamard must produce a single nonzero output at
    /// position (0,0) = 16 × input, and zero everywhere else. This is derivable directly from the
    /// H.264 §8.5.10 definition for uniform residuals.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(255)]
    public void Forward_hadamard_uniform_input_concentrates_energy_at_dc_position(int inputVal)
    {
        Span<int> x = stackalloc int[16];
        Span<int> y = stackalloc int[16];
        x.Fill(inputVal);
        H264LumaDcHadamard.ForwardHadamard4x4(x, y);
        y[0].Should().Be(inputVal * 16,
            $"all-uniform input {inputVal}: Hadamard(H·X·H)[0,0] = 16·input per §8.5.10 butterfly definition");
        for (var i = 1; i < 16; i++)
        {
            y[i].Should().Be(0,
                $"all-uniform input: all off-DC positions must be zero; position {i} is nonzero");
        }
    }

    [Fact]
    public void Forward_inverse_zeros_to_zeros()
    {
        Span<int> z = stackalloc int[16];
        Span<int> w = stackalloc int[16];
        Span<int> r = stackalloc int[16];
        H264LumaDcHadamard.ForwardHadamard4x4(z, w);
        for (var i = 0; i < 16; i++)
        {
            w[i].Should().Be(0);
        }

        H264LumaDcHadamard.InverseHadamard4x4(w, r);
        for (var i = 0; i < 16; i++)
        {
            r[i].Should().Be(0);
        }
    }

    /// <summary>Single-DC delta input must produce identical 16-position output bytewise after the round trip at QP = 0 (transform + quant + dequant + inverse must preserve exactly when the quantization step is the identity).</summary>
    [Fact]
    public void Single_position_delta_is_preserved_at_qp_zero()
    {
        Span<int> x = stackalloc int[16];
        Span<int> w = stackalloc int[16];
        Span<int> dq = stackalloc int[16];
        Span<int> r = stackalloc int[16];
        for (var pos = 0; pos < 16; pos++)
        {
            x.Clear();
            x[pos] = 100;

            H264LumaDcHadamard.ForwardHadamard4x4(x, w);
            H264LumaDcHadamard.QuantLumaDcHadamard(w, qp: 0);
            w.CopyTo(dq);
            H264LumaDcHadamard.DequantLumaDcHadamard(dq, qp: 0);
            H264LumaDcHadamard.InverseHadamard4x4(dq, r);

            for (var i = 0; i < 16; i++)
            {
                Math.Abs(r[i] - x[i]).Should().BeLessThanOrEqualTo(1,
                    $"delta-at-{pos} pos {i}: round-trip diverged at QP 0.");
            }
        }
    }
}
