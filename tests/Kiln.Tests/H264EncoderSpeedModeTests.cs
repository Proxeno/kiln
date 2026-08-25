using FluentAssertions;
using Kiln.RateControl;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Guards the <see cref="H264BaselineEncoderOptions.SpeedMode"/> preset ladder and its composition
/// contract: a mode only fills in the four speed knobs the caller never assigned, an explicit
/// assignment always wins (in either order), default options remain byte-identical to
/// <see cref="EncoderSpeedMode.HighQuality"/>, and a mode's stream is byte-identical to setting the
/// same knobs by hand — the mode is sugar, not a fifth code path.
/// </summary>
public sealed class H264EncoderSpeedModeTests
{
    private const int W = 320;
    private const int H = 240;
    private const int Frames = 6;

    [Fact]
    public void Default_options_report_historical_knob_values()
    {
        var options = new H264BaselineEncoderOptions();
        options.SpeedMode.Should().Be(EncoderSpeedMode.HighQuality);
        options.MaxReferenceFrames.Should().Be(2);
        options.UseMotionSatd.Should().BeTrue();
        options.SubPartitionRangeCap.Should().Be(16);
        options.MotionSearchEffortCapPerMb.Should().Be(0);
    }

    [Theory]
    [InlineData(EncoderSpeedMode.HighQuality, 2, true, 16, 0)]
    [InlineData(EncoderSpeedMode.Balanced, 1, true, 16, 512)]
    [InlineData(EncoderSpeedMode.Fast, 1, true, 8, 256)]
    [InlineData(EncoderSpeedMode.VeryFast, 1, false, 8, 128)]
    public void Mode_presets_fill_unassigned_knobs(
        EncoderSpeedMode mode, int maxRef, bool satd, int rangeCap, int effortCap)
    {
        var options = new H264BaselineEncoderOptions { SpeedMode = mode };
        options.MaxReferenceFrames.Should().Be(maxRef);
        options.UseMotionSatd.Should().Be(satd);
        options.SubPartitionRangeCap.Should().Be(rangeCap);
        options.MotionSearchEffortCapPerMb.Should().Be(effortCap);
    }

    [Fact]
    public void Explicit_assignment_wins_over_mode_in_either_order()
    {
        // Knob assigned before the mode.
        var before = new H264BaselineEncoderOptions { UseMotionSatd = true };
        before.SpeedMode = EncoderSpeedMode.VeryFast;
        before.UseMotionSatd.Should().BeTrue("an explicit assignment wins even when it re-states the default");
        before.MaxReferenceFrames.Should().Be(1, "unassigned knobs still come from the mode");

        // Knob assigned after the mode.
        var after = new H264BaselineEncoderOptions { SpeedMode = EncoderSpeedMode.VeryFast };
        after.MotionSearchEffortCapPerMb = 0;
        after.MotionSearchEffortCapPerMb.Should().Be(0, "an explicit 0 removes the preset's effort cap");
        after.UseMotionSatd.Should().BeFalse("unassigned knobs still come from the mode");
    }

    [Fact]
    public void Default_stream_is_byte_identical_to_explicit_high_quality()
    {
        var byDefault = EncodeAll(new H264BaselineEncoderOptions());
        var byMode = EncodeAll(new H264BaselineEncoderOptions { SpeedMode = EncoderSpeedMode.HighQuality });
        byMode.Should().Equal(byDefault);
    }

    [Theory]
    [InlineData(EncoderSpeedMode.Balanced)]
    [InlineData(EncoderSpeedMode.Fast)]
    [InlineData(EncoderSpeedMode.VeryFast)]
    public void Mode_stream_is_byte_identical_to_hand_set_knobs(EncoderSpeedMode mode)
    {
        var preset = new H264BaselineEncoderOptions { SpeedMode = mode };
        var byMode = EncodeAll(preset);
        var byHand = EncodeAll(new H264BaselineEncoderOptions
        {
            MaxReferenceFrames = preset.MaxReferenceFrames,
            UseMotionSatd = preset.UseMotionSatd,
            SubPartitionRangeCap = preset.SubPartitionRangeCap,
            MotionSearchEffortCapPerMb = preset.MotionSearchEffortCapPerMb,
        });
        byMode.Should().Equal(byHand, "a speed mode is exactly its documented knob preset");
    }

    [Theory]
    [InlineData(EncoderSpeedMode.Balanced)]
    [InlineData(EncoderSpeedMode.Fast)]
    [InlineData(EncoderSpeedMode.VeryFast)]
    public void Mode_streams_are_deterministic_across_runs(EncoderSpeedMode mode)
    {
        var first = EncodeAll(new H264BaselineEncoderOptions { SpeedMode = mode });
        var second = EncodeAll(new H264BaselineEncoderOptions { SpeedMode = mode });
        second.Should().Equal(first,
            "speed modes only compose effort-counted knobs, so identical inputs must yield identical bitstreams");
    }

    private static byte[] EncodeAll(H264BaselineEncoderOptions options)
    {
        options.QuantizationParameter = 26;
        options.KeyframeIntervalFrames = int.MaxValue;
        options.SliceCount = 2;
        var frames = TestFrames();
        var ys = W * H;
        var uv = ys / 4;
        var annex = new byte[ys * 2 + 262_144];
        var stream = new MemoryStream();
        using var enc = new H264BaselineEncoder(W, H, options);
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
    /// Deterministic diagonal scroll over a value-noise texture: enough real motion that every
    /// speed knob (reference choice, SATD scoring, sub-partition search, effort cap) participates,
    /// so the byte-identity assertions exercise the knobs rather than a skip-only stream.
    /// </summary>
    private static byte[][] TestFrames()
    {
        const int Cycle = 6;
        const int Step = 4;
        var ys = W * H;
        var uv = ys / 4;
        var margin = Step * Cycle;
        var texW = W + margin;
        var texH = H + margin;
        var tex = new byte[texW * texH];
        var rng = new Random(20260825);
        var latW = texW / 8 + 2;
        var latH = texH / 8 + 2;
        var lattice = new byte[latW * latH];
        rng.NextBytes(lattice);
        for (var y = 0; y < texH; y++)
        {
            for (var x = 0; x < texW; x++)
            {
                tex[y * texW + x] = (byte)(50 + lattice[(y / 8) * latW + x / 8] * 150 / 255);
            }
        }

        var frames = new byte[Cycle][];
        for (var f = 0; f < Cycle; f++)
        {
            var frame = new byte[ys + 2 * uv];
            var shift = f * Step;
            for (var row = 0; row < H; row++)
            {
                tex.AsSpan((row + shift) * texW + shift, W).CopyTo(frame.AsSpan(row * W, W));
            }

            frame.AsSpan(ys, uv).Fill(112);
            frame.AsSpan(ys + uv, uv).Fill(140);
            frames[f] = frame;
        }

        return frames;
    }
}
