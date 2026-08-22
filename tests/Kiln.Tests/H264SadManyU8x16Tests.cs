using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Batched 9-mode intra 4×4 SAD (<see cref="H264Intra4X4Simd.SadManyU8x16"/>) vs naive per-candidate SAD.
/// </summary>
public sealed class H264SadManyU8x16Tests
{
    private static void NaiveSadMany(ReadOnlySpan<byte> src, ReadOnlySpan<byte> predConcat, Span<int> sads, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var s = 0;
            for (var j = 0; j < 16; j++)
            {
                s += Math.Abs(src[j] - predConcat[i * 16 + j]);
            }

            sads[i] = s;
        }
    }

    [Fact]
    public void SadManyU8x16_matches_naive_for_nine_modes()
    {
        if (!H264Intra4X4Simd.IsSupported)
        {
            return;
        }

        EncoderSimdTestFacts.RequiresX64EncoderSimd();

        var rng = new Random(0x9A9D);
        var src = new byte[16];
        var pred = new byte[9 * 16];
        var simd = new int[9];
        var naive = new int[9];

        for (var trial = 0; trial < 40; trial++)
        {
            rng.NextBytes(src);
            rng.NextBytes(pred);
            H264Intra4X4Simd.SadManyU8x16(src, pred, simd, 9);
            NaiveSadMany(src, pred, naive, 9);
            simd.Should().Equal(naive, $"trial={trial}");
        }
    }

    [Fact]
    public void SadManyU8x16_max_contrast_all_modes()
    {
        if (!H264Intra4X4Simd.IsSupported)
        {
            return;
        }

        var src = new byte[16];
        var pred = new byte[9 * 16];
        Array.Fill(pred, (byte)255);
        var sads = new int[9];
        H264Intra4X4Simd.SadManyU8x16(src, pred, sads, 9);
        sads.Should().AllBeEquivalentTo(4080);
    }

    [Fact]
    public void SadManyU8x16_zero_when_predictions_match_source()
    {
        if (!H264Intra4X4Simd.IsSupported)
        {
            return;
        }

        var rng = new Random(7);
        var src = new byte[16];
        rng.NextBytes(src);
        var pred = new byte[9 * 16];
        for (var mode = 0; mode < 9; mode++)
        {
            src.CopyTo(pred.AsSpan(mode * 16, 16));
        }

        var sads = new int[9];
        H264Intra4X4Simd.SadManyU8x16(src, pred, sads, 9);
        sads.Should().AllBeEquivalentTo(0);
    }
}
