using Microsoft.Extensions.Logging.Abstractions;
using Kiln.Internal.H264.Adaptation;
using Kiln.RateControl;
using Xunit;

namespace Kiln.Tests.AdaptiveRateControlTests;

/// <summary>
/// Tests for the baseline-relative severe-congestion RTT test in <see cref="AdaptationPolicy"/>
/// (<see cref="RateControlConfig.SevereCongestionRttMultiplier"/> /
/// <see cref="RateControlConfig.SevereCongestionRttFloor"/>) and the baseline-relative recovery
/// gate (<see cref="RateControlConfig.RecoveryRttMultiplier"/>). The historical fixed 100 ms
/// severe threshold classified every link with a propagation RTT above it as permanently severe
/// (the same defect the ordinary congestion test had at 50 ms), and the fixed 40 ms recovery gate
/// meant such a link could cascade down but never walk back up.
/// </summary>
public sealed class SevereRttBaselineTests
{
    private static EncoderNetworkFeedback FeedbackWithRtt(double rttMs, double loss = 0.0) => new(
        EstimatedAvailableBitrateBps: 0,
        PacketLossRatio: loss,
        RoundTripTime: TimeSpan.FromMilliseconds(rttMs),
        Jitter: TimeSpan.Zero,
        PendingRtpBytes: 5_000,
        NackCount: 0,
        PictureLossIndication: false,
        FullIntraRequest: false,
        ClientDecodeDelay: null);

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

    /// <summary>Current decision at the top of every ladder with the slowest speed mode, so the
    /// stable branch has nothing to walk up (reason stays empty, no cooldown is armed) and the
    /// only possible change is a severe-tier speed-mode escalation.</summary>
    private static EncoderAdaptationDecision TopRungs() => new(
        TargetBitrateBps: 8_000_000, MaxFrameBytes: 30_000, TargetFps: 60, Width: 1920, Height: 1080,
        BaseQp: 28, ForceIdr: false, EnableIntraRefresh: false, SpeedMode: EncoderSpeedMode.HighQuality);

    private static AdaptationPolicy NewPolicy(RateControlConfig? config = null) =>
        new(config ?? new RateControlConfig(), new ResolutionLadder(), new FpsLadder(),
            NullLogger<AdaptationPolicy>.Instance);

    /// <summary>
    /// A satellite / cross-continent link whose propagation RTT is simply high (steady 150 ms, no
    /// loss, empty queue) is not severely congested: the baseline tracks 150 ms and 150 &lt;
    /// 150 * 3. The historical fixed 100 ms test put such a link in the severe tier on every
    /// single frame, walking it to the bottom of the speed/fps/resolution ladders and pinning it
    /// there.
    /// </summary>
    [Fact]
    public void SteadyHighRtt_IsNotSevere()
    {
        var policy = NewPolicy();
        for (var i = 0; i < 20; i++)
        {
            var d = policy.DecideAdaptation(FeedbackWithRtt(150), GoodStats(), TopRungs());
            Assert.Equal(EncoderSpeedMode.HighQuality, d.SpeedMode);
            Assert.Equal(1080, d.Height);
            Assert.Equal(60, d.Fps);
            Assert.Equal("", d.Reason);
        }
    }

    /// <summary>An RTT spike beyond baseline * multiplier and the floor is severe, wherever the
    /// baseline sits: 30 ms baseline spiking to 150 ms clears 30 * 3 and 100 ms.</summary>
    [Fact]
    public void SpikeAboveBaselineMultipleAndFloor_IsSevere()
    {
        var policy = NewPolicy();
        policy.DecideAdaptation(FeedbackWithRtt(30), GoodStats(), TopRungs()); // establish baseline

        var d = policy.DecideAdaptation(FeedbackWithRtt(150), GoodStats(), TopRungs());
        Assert.Equal("speed_mode_increase_severe_congestion", d.Reason);
        Assert.True(d.SpeedMode > EncoderSpeedMode.HighQuality, "severe spike should escalate speed mode");
    }

    /// <summary>
    /// On a fast link the absolute floor still protects against ordinary wobble: a 20 ms baseline
    /// spiking to 80 ms clears baseline * 3 but stays under the 100 ms floor — not severe, exactly
    /// as the historical absolute test behaved. 120 ms clears both and is severe.
    /// </summary>
    [Fact]
    public void Floor_StillGatesFastLinks()
    {
        var policy = NewPolicy();
        policy.DecideAdaptation(FeedbackWithRtt(20), GoodStats(), TopRungs());

        var under = policy.DecideAdaptation(FeedbackWithRtt(80), GoodStats(), TopRungs());
        Assert.Equal("", under.Reason);

        var over = policy.DecideAdaptation(FeedbackWithRtt(120), GoodStats(), TopRungs());
        Assert.Equal("speed_mode_increase_severe_congestion", over.Reason);
    }

    /// <summary>The multiplier is configurable: at 5, a 120 ms spike over a 30 ms baseline
    /// (4x) is not severe.</summary>
    [Fact]
    public void SevereMultiplier_IsConfigurable()
    {
        var policy = NewPolicy(new RateControlConfig { SevereCongestionRttMultiplier = 5 });
        policy.DecideAdaptation(FeedbackWithRtt(30), GoodStats(), TopRungs());

        var d = policy.DecideAdaptation(FeedbackWithRtt(120), GoodStats(), TopRungs());
        Assert.Equal("", d.Reason);
    }

    /// <summary>
    /// The recovery half of the same defect: a high-baseline link that legitimately cascaded down
    /// (loss episode) must be able to walk back up once the loss clears, even though its RTT will
    /// never drop under the historical absolute 40 ms recovery gate. With the baseline-relative
    /// gate, steady 150 ms against a 150 ms baseline counts as settled.
    /// </summary>
    [Fact]
    public void HighBaselineLink_WalksBackUp_AfterLossEpisode()
    {
        var policy = NewPolicy(new RateControlConfig { AdaptationCooldownFrames = 1 });
        var stats = GoodStats();

        policy.DecideAdaptation(FeedbackWithRtt(150), stats, TopRungs()); // establish baseline

        // Loss episode: severe via packet loss, speed mode escalates.
        var down = policy.DecideAdaptation(FeedbackWithRtt(150, loss: 0.15), stats, TopRungs());
        Assert.Equal("speed_mode_increase_severe_congestion", down.Reason);
        var degraded = TopRungs() with { SpeedMode = down.SpeedMode };

        // Loss clears; the link sits at its normal 150 ms. Walk-up must fire within a few frames
        // (one cooldown frame in between) — under the old 40 ms absolute gate it never would.
        AdaptationDecision recovered = null!;
        for (var i = 0; i < 4; i++)
        {
            recovered = policy.DecideAdaptation(FeedbackWithRtt(150), stats, degraded);
            if (recovered.Reason.Length > 0)
            {
                break;
            }
        }

        Assert.Equal("speed_mode_decrease_stable", recovered.Reason);
        Assert.Equal(EncoderSpeedMode.HighQuality, recovered.SpeedMode);
    }
}
