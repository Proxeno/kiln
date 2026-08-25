using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Kiln.RateControl;
using Kiln.Recovery;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// The encoder's measured content-activity signals
/// (<see cref="H264BaselineEncoder.LastFrameMotionComplexity"/> /
/// <see cref="H264BaselineEncoder.LastFrameTextureComplexity"/> /
/// <see cref="H264BaselineEncoder.LastFrameSceneChange"/>), their delivery through
/// <see cref="H264StreamingSession"/> into <see cref="EncoderPipelineStats"/>, and the recovery
/// policy's scene-change IDR response. These fields were previously hard-coded placeholders
/// (0.0 / 0.0 / false); the tests pin down that they now move with the content and that exactly
/// one consumer (the cooldown-guarded scene-change IDR) reacts.
/// </summary>
public sealed class H264ContentActivityTests
{
    private const int W = 320;
    private const int H = 240;

    private static (byte[] y, byte[] u, byte[] v) Planes(Func<int, int, byte> luma)
    {
        var y = new byte[W * H];
        for (var row = 0; row < H; row++)
        {
            for (var col = 0; col < W; col++)
            {
                y[row * W + col] = luma(col, row);
            }
        }

        var u = new byte[W * H / 4];
        var v = new byte[W * H / 4];
        u.AsSpan().Fill(120);
        v.AsSpan().Fill(130);
        return (y, u, v);
    }

    private static byte Textured(int x, int y) =>
        (byte)((x * 7 + y * 13 + ((x / 5) * (y / 3) % 47) * 3) & 0xFF);

    private static int Encode(H264BaselineEncoder enc, (byte[] y, byte[] u, byte[] v) f, byte[] annex) =>
        enc.EncodeFrame(f.y, f.u, f.v, W, W / 2, annex);

    // ── encoder-level signals ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Flat_static_content_reads_low_texture_zero_motion_no_scene_change()
    {
        using var enc = new H264BaselineEncoder(W, H, new H264BaselineEncoderOptions { QuantizationParameter = 28 });
        var annex = new byte[enc.RecommendedOutputBufferSize];
        var flat = Planes((_, _) => 128);

        Encode(enc, flat, annex); // IDR
        Encode(enc, flat, annex); // P
        enc.LastFrameTextureComplexity.Should().BeLessThan(0.05, "flat luma has ~zero MB variance");
        enc.LastFrameMotionComplexity.Should().Be(0.0, "static content codes with zero motion vectors");
        enc.LastFrameSceneChange.Should().BeFalse();
    }

    [Fact]
    public void Textured_content_reads_higher_texture_than_flat()
    {
        using var enc = new H264BaselineEncoder(W, H, new H264BaselineEncoderOptions { QuantizationParameter = 28 });
        var annex = new byte[enc.RecommendedOutputBufferSize];

        Encode(enc, Planes(Textured), annex);
        var textured = enc.LastFrameTextureComplexity;

        using var enc2 = new H264BaselineEncoder(W, H, new H264BaselineEncoderOptions { QuantizationParameter = 28 });
        Encode(enc2, Planes((_, _) => 128), annex);
        var flat = enc2.LastFrameTextureComplexity;

        textured.Should().BeGreaterThan(flat + 0.1, "busy texture must separate clearly from a flat plane");
    }

    [Fact]
    public void Panning_content_reads_nonzero_motion()
    {
        using var enc = new H264BaselineEncoder(W, H, new H264BaselineEncoderOptions { QuantizationParameter = 28 });
        var annex = new byte[enc.RecommendedOutputBufferSize];

        Encode(enc, Planes(Textured), annex); // IDR
        // Global 8-px horizontal pan: inter MBs should land on ~8 int-pel MVs.
        Encode(enc, Planes((x, y) => Textured(x + 8, y)), annex);

        enc.LastFrameMotionComplexity.Should().BeGreaterThan(0.1, "an 8-px global pan is real motion");
        enc.LastFrameSceneChange.Should().BeFalse("a pan is predictable content, not a cut");
    }

    [Fact]
    public void Hard_cut_on_P_frame_reads_scene_change_and_IDR_reads_false()
    {
        using var enc = new H264BaselineEncoder(W, H, new H264BaselineEncoderOptions
        {
            QuantizationParameter = 28,
            KeyframeIntervalFrames = 100,
        });
        var annex = new byte[enc.RecommendedOutputBufferSize];

        Encode(enc, Planes(Textured), annex); // IDR
        enc.LastFrameSceneChange.Should().BeFalse("scene change is a P-frame signal");
        Encode(enc, Planes(Textured), annex); // P, same scene
        enc.LastFrameSceneChange.Should().BeFalse();

        // Hard cut: unrelated content with opposite structure.
        Encode(enc, Planes((x, y) => (byte)(255 - Textured(y, x))), annex);
        enc.LastFrameSceneChange.Should().BeTrue("a cut codes a majority of P-frame MBs as intra");

        // The frame after the cut, same new scene: prediction works again.
        Encode(enc, Planes((x, y) => (byte)(255 - Textured(y, x))), annex);
        enc.LastFrameSceneChange.Should().BeFalse();
    }

    [Fact]
    public void Motion_complexity_carries_across_an_IDR()
    {
        using var enc = new H264BaselineEncoder(W, H, new H264BaselineEncoderOptions
        {
            QuantizationParameter = 28,
            KeyframeIntervalFrames = 100,
        });
        var annex = new byte[enc.RecommendedOutputBufferSize];

        Encode(enc, Planes(Textured), annex); // IDR
        Encode(enc, Planes((x, y) => Textured(x + 8, y)), annex); // P with motion
        var motion = enc.LastFrameMotionComplexity;
        motion.Should().BeGreaterThan(0.0);

        Encode(enc, Planes((x, y) => Textured(x + 16, y)), annex, forceKeyframe: true);
        enc.LastFrameMotionComplexity.Should().Be(motion, "an IDR has no inter decisions to measure; the signal carries over");

        static int Encode(H264BaselineEncoder enc, (byte[] y, byte[] u, byte[] v) f, byte[] annex, bool forceKeyframe = false) =>
            enc.EncodeFrame(f.y, f.u, f.v, W, W / 2, annex, forceKeyframe: forceKeyframe);
    }

    // ── recovery policy: the scene-change consumer ───────────────────────────────────────────────

    private static EncoderNetworkFeedback CleanFeedback() => new(
        EstimatedAvailableBitrateBps: 10_000_000,
        PacketLossRatio: 0.0,
        RoundTripTime: TimeSpan.FromMilliseconds(20),
        Jitter: TimeSpan.FromMilliseconds(2),
        PendingRtpBytes: 5_000,
        NackCount: 0,
        PictureLossIndication: false,
        FullIntraRequest: false,
        ClientDecodeDelay: null);

    private static EncoderPipelineStats StatsWithSceneChange(bool sceneChange) => new(
        LastEncodeDuration: TimeSpan.FromMilliseconds(3),
        AverageEncodeDuration: TimeSpan.FromMilliseconds(3),
        PendingInputFrames: 0,
        PendingEncodedFrames: 0,
        DroppedInputFrames: 0,
        DroppedEncodedFrames: 0,
        LastEncodedFrameBytes: 15_000,
        LastFrameQp: 28,
        LastFrameWasIdr: false,
        MotionComplexity: 0.5,
        TextureComplexity: 0.5,
        SceneChangeDetected: sceneChange);

    private static EncoderAdaptationDecision AnyDecision() => new(
        TargetBitrateBps: 8_000_000, MaxFrameBytes: 30_000, TargetFps: 30, Width: W, Height: H,
        BaseQp: 28, ForceIdr: false, EnableIntraRefresh: false,
        SpeedMode: EncoderSpeedMode.Balanced);

    [Fact]
    public void SceneChange_forces_IDR_when_cooldown_clear()
    {
        var policy = new H264RecoveryPolicy(new RateControlConfig(), NullLogger<H264RecoveryPolicy>.Instance);
        var decision = policy.DecideRecovery(CleanFeedback(), AnyDecision(), StatsWithSceneChange(true));
        decision.ForceIdr.Should().BeTrue();
        decision.RecoveryReason.Should().Be("scene_change");
        decision.EnableIntraRefresh.Should().BeFalse();
    }

    [Fact]
    public void SceneChange_in_cooldown_does_nothing()
    {
        var policy = new H264RecoveryPolicy(
            new RateControlConfig { IdrCooldownFrames = 60 }, NullLogger<H264RecoveryPolicy>.Instance);
        // Enter cooldown via a PLI-driven IDR.
        policy.DecideRecovery(CleanFeedback() with { PictureLossIndication = true }, AnyDecision()).ForceIdr
            .Should().BeTrue();

        var decision = policy.DecideRecovery(CleanFeedback(), AnyDecision(), StatsWithSceneChange(true));
        decision.ForceIdr.Should().BeFalse("scene-change IDRs respect the cooldown");
        decision.EnableIntraRefresh.Should().BeFalse("a cut is not a loss event; no intra-refresh fallback");
        decision.RecoveryReason.Should().BeEmpty();
    }

    [Fact]
    public void FIR_outranks_scene_change()
    {
        var policy = new H264RecoveryPolicy(new RateControlConfig(), NullLogger<H264RecoveryPolicy>.Instance);
        var decision = policy.DecideRecovery(
            CleanFeedback() with { FullIntraRequest = true }, AnyDecision(), StatsWithSceneChange(true));
        decision.ForceIdr.Should().BeTrue();
        decision.RecoveryReason.Should().Be("FIR_requested");
    }

    // ── session end-to-end: cut → reported signal → IDR on the next frame ────────────────────────

    [Fact]
    public void Session_answers_a_scene_cut_with_an_IDR_on_the_next_frame()
    {
        using var session = new H264StreamingSession(W, H, rateControlConfig: new RateControlConfig
        {
            SupportedWidths = [W],
            SupportedHeights = [H],
            SupportedFps = [30],
            IdrCooldownFrames = 4,
        });
        var annex = new byte[session.RecommendedOutputBufferSize];
        var sceneA = Planes(Textured);
        var sceneB = Planes((x, y) => (byte)(255 - Textured(y, x)));

        H264StreamingEncodeResult Encode((byte[] y, byte[] u, byte[] v) f) =>
            session.EncodeFrame(f.y, f.u, f.v, W, W / 2, annex, CleanFeedback());

        Encode(sceneA).WasIdr.Should().BeTrue("first frame is the scheduled IDR");
        for (var i = 0; i < 6; i++)
        {
            var r = Encode(sceneA);
            r.WasIdr.Should().BeFalse();
            r.SceneChangeDetected.Should().BeFalse();
        }

        var cut = Encode(sceneB);
        cut.WasIdr.Should().BeFalse("the cut frame itself is coded before the signal exists");
        cut.SceneChangeDetected.Should().BeTrue("the mostly-intra P frame reports the cut");

        var next = Encode(sceneB);
        next.WasIdr.Should().BeTrue("the recovery policy answers the reported cut with an IDR");
        next.SceneChangeDetected.Should().BeFalse("an IDR never reports a scene change");
    }
}
