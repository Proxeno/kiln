using FluentAssertions;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Guards the deterministic ME effort budget (<see cref="H264BaselineEncoderOptions.MotionSearchEffortCapPerMb"/>):
/// the degradation ladder is driven by a count of algorithmic work, never wall clock, so two encodes
/// of the same input must produce byte-identical bitstreams even when the budget binds mid-slice.
/// </summary>
public sealed class H264MotionEffortBudgetTests
{
    private const int W = 320;
    private const int H = 240;
    private const int Frames = 8;

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Capped_encode_is_deterministic_across_runs(int slices)
    {
        var first = EncodeAll(slices, effortCap: 24);
        var second = EncodeAll(slices, effortCap: 24);
        second.Should().Equal(first,
            "the effort budget counts algorithmic work, so identical inputs must yield identical bitstreams");
    }

    [Fact]
    public void Cap_engages_all_tiers_on_divergent_motion()
    {
        Kiln.Internal.H264.H264PInterDiagnostics.CollectPhaseCounts = true;
        Kiln.Internal.H264.H264PInterDiagnostics.ResetPhaseCounts();
        EncodeAll(slices: 4, effortCap: 24);
        var (tier1, tier2, tier3) = Kiln.Internal.H264.H264PInterDiagnostics.ReadMeBudgetTierCounts();
        Kiln.Internal.H264.H264PInterDiagnostics.CollectPhaseCounts = false;
        (tier1 + tier2 + tier3).Should().BeGreaterThan(0, "a 24 units/MB cap must bind on divergent motion");
        tier3.Should().BeGreaterThan(0, "the 16x16-only floor tier must be exercised");
    }

    private static byte[] EncodeAll(int slices, int effortCap)
    {
        var frames = GenerateDivergentMotion(W, H);
        var ys = W * H;
        var uv = ys / 4;
        var annex = new byte[ys * 2 + 262_144];
        var stream = new MemoryStream();
        using var enc = new Kiln.H264BaselineEncoder(W, H, new Kiln.H264BaselineEncoderOptions
        {
            QuantizationParameter = 26,
            KeyframeIntervalFrames = int.MaxValue,
            LevelIdc = 40,
            SliceCount = slices,
            MotionSearchEffortCapPerMb = effortCap,
        });
        for (var i = 0; i < Frames; i++)
        {
            var f = frames[i % frames.Length];
            var n = enc.EncodeFrame(
                f.AsSpan(0, ys), f.AsSpan(ys, uv), f.AsSpan(ys + uv, uv), W, W / 2, annex, forceKeyframe: i == 0);
            stream.Write(annex, 0, n);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Opposing-halves scroll (top half left, bottom half right, 6 px/frame) over a value-noise
    /// texture: macroblocks straddling the mid-line have genuinely divergent per-quadrant motion,
    /// the content class that maximises sub-partition search cost and exercises every budget tier.
    /// </summary>
    private static byte[][] GenerateDivergentMotion(int w, int h)
    {
        const int Cycle = 8;
        const int Step = 6;
        var ys = w * h;
        var uv = ys / 4;
        var margin = Step * Cycle;
        var texW = w + 2 * margin;
        var texH = h;
        var tex = new byte[texW * texH];
        var rng = new Random(1729);
        var latW = texW / 8 + 2;
        var latH = texH / 8 + 2;
        var lattice = new byte[latW * latH];
        rng.NextBytes(lattice);
        for (var y = 0; y < texH; y++)
        {
            for (var x = 0; x < texW; x++)
            {
                var v = lattice[(y / 8) * latW + x / 8];
                tex[y * texW + x] = (byte)(48 + (v * 150 / 255) + (((x / 4) + (y / 4)) & 1) * 16);
            }
        }

        var frames = new byte[Cycle][];
        for (var f = 0; f < Cycle; f++)
        {
            var frame = new byte[ys + 2 * uv];
            var yPlane = frame.AsSpan(0, ys);
            var shift = f * Step;
            for (var row = 0; row < h; row++)
            {
                // Top half scrolls left, bottom half scrolls right.
                var offset = row < h / 2 ? margin + shift : margin - shift;
                tex.AsSpan(row * texW + offset, w).CopyTo(yPlane.Slice(row * w, w));
            }

            frame.AsSpan(ys, uv).Fill(112);
            frame.AsSpan(ys + uv, uv).Fill(144);
            frames[f] = frame;
        }

        return frames;
    }
}
