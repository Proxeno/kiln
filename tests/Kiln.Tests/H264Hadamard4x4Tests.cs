using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Parity and correctness tests for <see cref="H264Hadamard4x4.Satd"/>.
/// Gate 1: flat-zero residual produces SATD=0.
/// Gate 2: hand-computed reference for a known non-zero residual.
/// Gate 3: symmetry — swapping src and pred gives the same SATD (|H(s−p)| = |H(p−s)| because abs).
/// </summary>
public sealed class H264Hadamard4x4Tests
{
    // Reference implementation: 2D Hadamard via explicit H·r·Hᵀ matrix product, then sum|coeff|>>1.
    private static int ReferenceSatd(ReadOnlySpan<byte> src16, ReadOnlySpan<byte> pred16)
    {
        // H matrix rows (standard Walsh-Hadamard 4×4)
        int[,] H =
        {
            { 1,  1,  1,  1 },
            { 1,  1, -1, -1 },
            { 1, -1, -1,  1 },
            { 1, -1,  1, -1 },
        };

        // residual r[4×4]
        int[,] r = new int[4, 4];
        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
                r[i, j] = src16[i * 4 + j] - pred16[i * 4 + j];

        // tmp = H · r
        int[,] tmp = new int[4, 4];
        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
            {
                var s = 0;
                for (var k = 0; k < 4; k++)
                    s += H[i, k] * r[k, j];
                tmp[i, j] = s;
            }

        // out = tmp · Hᵀ = tmp · H (H is symmetric)
        int[,] outM = new int[4, 4];
        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
            {
                var s = 0;
                for (var k = 0; k < 4; k++)
                    s += tmp[i, k] * H[j, k];
                outM[i, j] = s;
            }

        var sum = 0;
        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
                sum += Math.Abs(outM[i, j]);
        return (sum + 1) >> 1;
    }

    [Fact]
    public void Flat_zero_residual_produces_satd_zero()
    {
        var src = new byte[16];
        var pred = new byte[16];
        // Both all-zero → SATD = 0
        H264Hadamard4x4.Satd(src, pred).Should().Be(0, "zero residual has zero SATD");
    }

    [Fact]
    public void Flat_same_constant_produces_satd_zero()
    {
        var src = new byte[16];
        var pred = new byte[16];
        for (var i = 0; i < 16; i++) { src[i] = 128; pred[i] = 128; }
        H264Hadamard4x4.Satd(src, pred).Should().Be(0, "identical blocks have zero SATD");
    }

    /// <summary>
    /// Known non-zero block: src = [100,0,0,...], pred = all zeros.
    /// Hand-compute: residual has a single 100 at [0,0]; all 16 transform outputs have magnitude 100;
    /// sum = 16 × 100 = 1600; SATD = (1600+1)>>1 = 800.
    /// </summary>
    [Fact]
    public void Single_nonzero_residual_at_corner_matches_hand_computation()
    {
        var src = new byte[16];
        var pred = new byte[16];
        src[0] = 100;
        // H·r·Hᵀ with r=(100,0,...,0): each output = H[i,0] * 100 * H[j,0] = 100 for all i,j.
        // sum|coeff| = 16 * 100 = 1600; SATD = (1600+1)>>1 = 800.
        H264Hadamard4x4.Satd(src, pred).Should().Be(800);
    }

    /// <summary>Parity: fast butterfly matches the reference H·r·Hᵀ matrix product for random inputs.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(2026)]
    public void Butterfly_matches_matrix_reference_for_random_blocks(int seed)
    {
        var rng = new Random(seed);
        var src = new byte[16];
        var pred = new byte[16];

        for (var trial = 0; trial < 100; trial++)
        {
            rng.NextBytes(src);
            rng.NextBytes(pred);

            var got = H264Hadamard4x4.Satd(src, pred);
            var expected = ReferenceSatd(src, pred);

            got.Should().Be(expected,
                $"trial {trial} seed={seed}: fast butterfly must match reference H·r·Hᵀ");
        }
    }

    [Theory]
    [InlineData(7)]
    [InlineData(99)]
    public void Satd_is_symmetric_swapping_src_and_pred(int seed)
    {
        var rng = new Random(seed);
        var a = new byte[16];
        var b = new byte[16];

        for (var trial = 0; trial < 50; trial++)
        {
            rng.NextBytes(a);
            rng.NextBytes(b);
            // |H(a-b)| = |H(b-a)| because abs
            H264Hadamard4x4.Satd(a, b).Should().Be(H264Hadamard4x4.Satd(b, a),
                $"trial {trial}: SATD must be symmetric");
        }
    }

    [Fact]
    public void Uniform_nonzero_residual_concentrates_energy_at_dc()
    {
        // Residual all-constant c: only the [0,0] DC output is nonzero (= 16c),
        // all other 15 coefficients are zero. sum=16c; SATD = (16c + 1) >> 1.
        // Derivation: H·r·H^T for uniform r[k,l]=c: only row 0 of H has nonzero sum (=4),
        // so output[i,j] = 0 unless i=j=0, where output[0,0] = 4*c*4 = 16c.
        const int c = 10;
        var src = new byte[16];
        var pred = new byte[16];
        for (var i = 0; i < 16; i++) src[i] = (byte)(100 + c);
        for (var i = 0; i < 16; i++) pred[i] = 100;

        var got = H264Hadamard4x4.Satd(src, pred);
        var expected = ReferenceSatd(src, pred);
        got.Should().Be(expected, "uniform residual must match reference");
        // DC-only: single nonzero output = 16c=160, sum=160; SATD = (160+1)>>1 = 80.
        got.Should().Be((16 * c + 1) >> 1);
    }
}
