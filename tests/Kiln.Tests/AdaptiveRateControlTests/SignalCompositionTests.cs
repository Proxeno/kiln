using Kiln.Internal.H264.Adaptation;
using Kiln.RateControl;
using Xunit;

namespace Kiln.Tests.AdaptiveRateControlTests;

/// <summary>
/// The newly-wired <see cref="EncoderNetworkFeedback"/> signals firing together — where a
/// controller usually goes wrong. Each test pins the composition rule: the bandwidth-estimate
/// ceiling and the loss heuristics take the minimum; a jitter hold never cancels a cap or a cut;
/// client decode delay moves complexity but never bitrate, and severe congestion owns the cascade
/// label (and its cooldown) when both want it.
/// </summary>
public sealed class SignalCompositionTests
{
    private static EncoderNetworkFeedback Feedback(
        int estimateBps = 0, double loss = 0.0, double rttMs = 20, double jitterMs = 0,
        int queueBytes = 5_000, double? decodeDelayMs = null) => new(
        EstimatedAvailableBitrateBps: estimateBps,
        PacketLossRatio: loss,
        RoundTripTime: TimeSpan.FromMilliseconds(rttMs),
        Jitter: TimeSpan.FromMilliseconds(jitterMs),
        PendingRtpBytes: queueBytes,
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

    private static H264AdaptiveRateController Master(RateControlConfig? config = null) =>
        new(config ?? new RateControlConfig { StabilityWindowFrames = 5, AdaptationCooldownFrames = 2 });

    private static EncoderAdaptationDecision Drive(
        H264AdaptiveRateController ctrl, EncoderNetworkFeedback feedback, EncoderPipelineStats stats)
    {
        var d = ctrl.GetDecision(feedback, stats);
        ctrl.SyncAppliedState(d.Width, d.Height, d.TargetFps, d.SpeedMode);
        return d;
    }

    /// <summary>
    /// Estimate collapse + jitter spike: the cap must land immediately (the jitter hold defers
    /// increases, never a cap), the held target must sit exactly at the estimate, and once both
    /// clear, recovery walks up gradually and never overshoots the restored estimate.
    /// </summary>
    [Fact]
    public void EstimateCollapse_And_JitterSpike_CapThenHoldThenRecover()
    {
        var ctrl = Master();
        var stats = GoodStats();

        Drive(ctrl, Feedback(estimateBps: 10_000_000, jitterMs: 2), stats); // baselines
        for (var i = 0; i < 10; i++)
        {
            var d = Drive(ctrl, Feedback(estimateBps: 3_000_000, jitterMs: 30), stats);
            Assert.Equal(3_000_000, d.TargetBitrateBps); // capped at once, held there — no cut below
        }

        EncoderAdaptationDecision last = null!;
        for (var i = 0; i < 40; i++)
        {
            last = Drive(ctrl, Feedback(estimateBps: 10_000_000, jitterMs: 2), stats);
            Assert.True(last.TargetBitrateBps <= 10_000_000);
        }

        Assert.True(last.TargetBitrateBps > 3_000_000, "recovery must walk up once jitter settles");
    }

    /// <summary>Loss below a depressed estimate: the heuristics keep cutting under the ceiling —
    /// the target is the minimum of the two, not whichever signal spoke last.</summary>
    [Fact]
    public void Loss_KeepsCutting_BelowTheEstimateCeiling()
    {
        var ctrl = Master(new RateControlConfig { InitialTargetBitrateBps = 8_000_000, DownshiftFactor = 0.7 });
        var stats = GoodStats();

        Assert.Equal(4_000_000, Drive(ctrl, Feedback(estimateBps: 4_000_000), stats).TargetBitrateBps);
        Assert.Equal(2_800_000, Drive(ctrl, Feedback(estimateBps: 4_000_000, loss: 0.05), stats).TargetBitrateBps);
        // (int)(2_800_000 * 0.7) truncates: 0.7 is not exact in binary floating point.
        Assert.Equal(1_959_999, Drive(ctrl, Feedback(estimateBps: 4_000_000, loss: 0.05), stats).TargetBitrateBps);
    }

    /// <summary>
    /// Severe congestion + client behind: both want the cascade, and it must fire once per
    /// cooldown with the severe label (the stronger claim), while bitrate downshifts from the
    /// congestion path — one rung and one cut per frame, no double-escalation.
    /// </summary>
    [Fact]
    public void SevereCongestion_And_ClientBehind_CascadeOnce_WithSevereLabel()
    {
        var ctrl = Master();
        var stats = GoodStats();

        Drive(ctrl, Feedback(), stats); // settle: walks speed to HighQuality, arms cooldown (2)
        Drive(ctrl, Feedback(), stats);
        Drive(ctrl, Feedback(), stats);

        var before = Drive(ctrl, Feedback(), stats);
        var d = Drive(ctrl, Feedback(loss: 0.15, decodeDelayMs: 30), stats);

        Assert.Equal("speed_mode_increase_severe_congestion", ctrl.LastAdaptationReason);
        Assert.Equal((int)before.SpeedMode + 1, (int)d.SpeedMode); // exactly one rung
        Assert.True(d.TargetBitrateBps < before.TargetBitrateBps, "congestion still cuts bitrate");
    }

    /// <summary>
    /// Jitter spike + client behind, network otherwise healthy: bitrate is held (jitter tempers
    /// increases, decode delay never touches bitrate) while complexity still comes down for the
    /// client — the two "don't cut bandwidth" signals must not deadlock the client's relief.
    /// </summary>
    [Fact]
    public void JitterSpike_And_ClientBehind_HoldBitrate_ButRelieveClient()
    {
        var ctrl = Master();
        var stats = GoodStats();

        var initial = Drive(ctrl, Feedback(jitterMs: 2), stats);
        EncoderAdaptationDecision last = null!;
        for (var i = 0; i < 10; i++)
        {
            last = Drive(ctrl, Feedback(jitterMs: 30, decodeDelayMs: 30), stats);
            Assert.Equal(initial.TargetBitrateBps, last.TargetBitrateBps);
        }

        Assert.True(last.SpeedMode > initial.SpeedMode, "the client must still get complexity relief");
        Assert.Equal("speed_mode_increase_client_decode_delay", ctrl.LastAdaptationReason);
    }

    /// <summary>
    /// Everything at once on a high-baseline link: steady 150 ms RTT (not severe — baseline-
    /// relative), estimate collapsed, jitter spiking, client behind. The target obeys the
    /// ceiling, the cascade names the client (the RTT never qualifies), and the whole composite
    /// is deterministic — two identical runs, identical decisions.
    /// </summary>
    [Fact]
    public void AllSignals_Composed_AreCoherentAndDeterministic()
    {
        var stats = GoodStats();

        List<EncoderAdaptationDecision> Run()
        {
            var ctrl = Master();
            var decisions = new List<EncoderAdaptationDecision>();
            decisions.Add(Drive(ctrl, Feedback(estimateBps: 10_000_000, rttMs: 150, jitterMs: 5), stats));
            for (var i = 0; i < 20; i++)
            {
                decisions.Add(Drive(
                    ctrl,
                    Feedback(estimateBps: 2_000_000, rttMs: 150, jitterMs: 25, decodeDelayMs: 40),
                    stats));
            }

            return decisions;
        }

        var run = Run();
        foreach (var d in run.Skip(1))
        {
            Assert.Equal(2_000_000, d.TargetBitrateBps); // ceiling holds; no severe-RTT bitrate spiral
        }

        Assert.True(
            run[^1].SpeedMode > run[0].SpeedMode,
            "the client-behind cascade must still deliver complexity relief under the composite");
        Assert.Equal(run, Run());
    }
}
