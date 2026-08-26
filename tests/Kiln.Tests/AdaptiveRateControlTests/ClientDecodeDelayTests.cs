using Microsoft.Extensions.Logging.Abstractions;
using Kiln.Internal.H264.Adaptation;
using Kiln.RateControl;
using Xunit;

namespace Kiln.Tests.AdaptiveRateControlTests;

/// <summary>
/// Tests for <see cref="EncoderNetworkFeedback.ClientDecodeDelay"/>: a client that cannot decode
/// within the frame interval (<see cref="RateControlConfig.ClientDecodeDelayBudgetFactor"/>) is a
/// decode-capacity problem, not network congestion — the controller answers with the complexity
/// cascade (speed mode → fps → resolution) and never with a bitrate cut.
/// </summary>
public sealed class ClientDecodeDelayTests
{
    private static EncoderNetworkFeedback Healthy(double? decodeDelayMs = null) => new(
        EstimatedAvailableBitrateBps: 0,
        PacketLossRatio: 0.0,
        RoundTripTime: TimeSpan.FromMilliseconds(20),
        Jitter: TimeSpan.Zero,
        PendingRtpBytes: 5_000,
        NackCount: 0,
        PictureLossIndication: false,
        FullIntraRequest: false,
        ClientDecodeDelay: decodeDelayMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null);

    private static EncoderPipelineStats GoodStats() => new(
        LastEncodeDuration: TimeSpan.FromMilliseconds(3),
        AverageEncodeDuration: TimeSpan.FromMilliseconds(3),
        PendingInputFrames: 1,
        PendingEncodedFrames: 0,
        DroppedInputFrames: 0,
        DroppedEncodedFrames: 0,
        LastEncodedFrameBytes: 15_000,
        LastFrameQp: 28,
        LastFrameWasIdr: false,
        MotionComplexity: 0.2,
        TextureComplexity: 0.3,
        SceneChangeDetected: false);

    private static EncoderAdaptationDecision Decision(int w, int h, int fps, EncoderSpeedMode speed) => new(
        TargetBitrateBps: 8_000_000, MaxFrameBytes: 30_000, TargetFps: fps, Width: w, Height: h,
        BaseQp: 28, ForceIdr: false, EnableIntraRefresh: false, SpeedMode: speed);

    private static AdaptationPolicy NewPolicy(RateControlConfig? config = null) =>
        new(config ?? new RateControlConfig(), new ResolutionLadder(), new FpsLadder(),
            NullLogger<AdaptationPolicy>.Instance);

    /// <summary>A decode delay beyond the frame interval (30 ms against 16.7 ms at 60 fps) drives
    /// the complexity cascade even though the network is perfectly healthy — and the reason string
    /// names the client, not congestion.</summary>
    [Fact]
    public void DecodeDelayBeyondBudget_EscalatesSpeedMode_OnHealthyNetwork()
    {
        var d = NewPolicy().DecideAdaptation(
            Healthy(decodeDelayMs: 30), GoodStats(), Decision(1920, 1080, 60, EncoderSpeedMode.Balanced));
        Assert.Equal("speed_mode_increase_client_decode_delay", d.Reason);
        Assert.True(d.SpeedMode > EncoderSpeedMode.Balanced);
        Assert.Equal(1080, d.Height);
        Assert.Equal(60, d.Fps);
    }

    /// <summary>With speed mode already maxed the cascade reduces fps (more time per frame for the
    /// decoder), then resolution (less work per frame).</summary>
    [Fact]
    public void Cascade_ReachesFpsThenResolution()
    {
        var fpsStep = NewPolicy().DecideAdaptation(
            Healthy(decodeDelayMs: 30), GoodStats(), Decision(1920, 1080, 60, EncoderSpeedMode.VeryFast));
        Assert.Equal("fps_reduction_client_decode_delay", fpsStep.Reason);
        Assert.Equal(30, fpsStep.Fps);

        var resStep = NewPolicy().DecideAdaptation(
            Healthy(decodeDelayMs: 100), GoodStats(), Decision(1920, 1080, 15, EncoderSpeedMode.VeryFast));
        Assert.Equal("resolution_reduction_client_decode_delay", resStep.Reason);
        Assert.Equal(900, resStep.Height);
    }

    /// <summary>The budget is relative to the current frame interval: 30 ms is beyond budget at
    /// 60 fps but within it at 30 fps, so an fps reduction clears the signal naturally.</summary>
    [Fact]
    public void Budget_IsRelativeToFrameInterval()
    {
        var at30Fps = NewPolicy().DecideAdaptation(
            Healthy(decodeDelayMs: 30), GoodStats(), Decision(1920, 1080, 30, EncoderSpeedMode.Balanced));
        Assert.Equal("", at30Fps.Reason);
    }

    /// <summary>Null (the record default) and a delay within budget leave the policy untouched.</summary>
    [Fact]
    public void NullOrInBudgetDelay_HasNoEffect()
    {
        var top = Decision(1920, 1080, 60, EncoderSpeedMode.HighQuality);
        Assert.Equal("", NewPolicy().DecideAdaptation(Healthy(), GoodStats(), top).Reason);
        Assert.Equal("", NewPolicy().DecideAdaptation(Healthy(decodeDelayMs: 8), GoodStats(), top).Reason);
    }

    /// <summary>
    /// The walk-up defers while the client is merely marginal: recovery requires the delay under
    /// half the cascade budget, so a decoder that only just caught up is not immediately handed
    /// more work. Comfortably under, the walk-up resumes.
    /// </summary>
    [Fact]
    public void WalkUp_RequiresComfortableDecodeHeadroom()
    {
        var degraded = Decision(1280, 720, 60, EncoderSpeedMode.Balanced);

        // 12 ms at 60 fps: under the 16.7 ms cascade budget, over the 8.3 ms recovery budget.
        var marginal = NewPolicy().DecideAdaptation(Healthy(decodeDelayMs: 12), GoodStats(), degraded);
        Assert.Equal("", marginal.Reason);
        Assert.Equal(720, marginal.Height);

        var comfortable = NewPolicy().DecideAdaptation(Healthy(decodeDelayMs: 5), GoodStats(), degraded);
        Assert.Equal("resolution_increase_stable", comfortable.Reason);
        Assert.Equal(900, comfortable.Height);
    }

    /// <summary>
    /// The end-to-end contract, master-controller level: a struggling client on a healthy network
    /// gets complexity relief (speed mode moves) while the bitrate path is untouched — identical
    /// frame for frame to a run that never reported a decode delay. Congestion cuts bitrate;
    /// decode delay must not.
    /// </summary>
    [Fact]
    public void MasterController_ReducesComplexity_WithoutTouchingBitrate()
    {
        var stats = GoodStats();

        List<EncoderAdaptationDecision> Run(double? decodeDelayMs)
        {
            var ctrl = new H264AdaptiveRateController(new RateControlConfig { AdaptationCooldownFrames = 2 });
            var decisions = new List<EncoderAdaptationDecision>();
            for (var i = 0; i < 30; i++)
            {
                var d = ctrl.GetDecision(Healthy(decodeDelayMs), stats);
                ctrl.SyncAppliedState(d.Width, d.Height, d.TargetFps, d.SpeedMode);
                decisions.Add(d);
            }

            return decisions;
        }

        var withDelay = Run(30);
        var without = Run(null);

        Assert.Equal(without.Select(d => d.TargetBitrateBps), withDelay.Select(d => d.TargetBitrateBps));
        Assert.Equal(without.Select(d => d.BaseQp), withDelay.Select(d => d.BaseQp));
        Assert.True(
            withDelay[^1].SpeedMode > without[^1].SpeedMode,
            $"the struggling client must get a faster speed mode (got {withDelay[^1].SpeedMode} vs {without[^1].SpeedMode})");
    }
}
