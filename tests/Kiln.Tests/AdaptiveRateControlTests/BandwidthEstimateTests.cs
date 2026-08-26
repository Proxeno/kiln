using Microsoft.Extensions.Logging.Abstractions;
using Kiln.RateControl;
using Xunit;

namespace Kiln.Tests.AdaptiveRateControlTests;

/// <summary>
/// Tests for <see cref="EncoderNetworkFeedback.EstimatedAvailableBitrateBps"/> as a hard ceiling
/// on the target bitrate: a caller-supplied transport estimate (GCC / transport-cc / REMB) caps
/// the target immediately when it collapses, composes with the loss/RTT heuristics (which keep
/// driving reductions below it), and releases the ceiling for the ordinary stability-window
/// upshift when it recovers. Non-positive is the "no estimate" sentinel — the heuristics-only
/// path, byte-for-byte the pre-wiring behaviour (see <c>V050EquivalenceTests</c>).
/// </summary>
public sealed class BandwidthEstimateTests
{
    private static EncoderNetworkFeedback Feedback(int estimateBps, double loss = 0.0) => new(
        EstimatedAvailableBitrateBps: estimateBps,
        PacketLossRatio: loss,
        RoundTripTime: TimeSpan.FromMilliseconds(30),
        Jitter: TimeSpan.Zero,
        PendingRtpBytes: 5_000,
        NackCount: 0,
        PictureLossIndication: false,
        FullIntraRequest: false,
        ClientDecodeDelay: null);

    private static EncoderPipelineStats NormalStats() => new(
        LastEncodeDuration: TimeSpan.FromMilliseconds(3),
        AverageEncodeDuration: TimeSpan.FromMilliseconds(3),
        PendingInputFrames: 1,
        PendingEncodedFrames: 0,
        DroppedInputFrames: 0,
        DroppedEncodedFrames: 0,
        LastEncodedFrameBytes: 15_000,
        LastFrameQp: 28,
        LastFrameWasIdr: false,
        MotionComplexity: 0.3,
        TextureComplexity: 0.5,
        SceneChangeDetected: false);

    private static LowLatencyRateController Controller(RateControlConfig? config = null) =>
        new(config ?? new RateControlConfig(), NullLogger<LowLatencyRateController>.Instance);

    /// <summary>A collapsing estimate caps the target on the very next decision — no waiting for
    /// loss or RTT symptoms, and no multiplicative walk down through many frames.</summary>
    [Fact]
    public void EstimateCollapse_CapsTargetImmediately()
    {
        var controller = Controller(new RateControlConfig { InitialTargetBitrateBps = 8_000_000 });
        var stats = NormalStats();

        Assert.Equal(8_000_000, controller.Decide(Feedback(10_000_000), stats).TargetBitrateBps);

        var capped = controller.Decide(Feedback(2_000_000), stats);
        Assert.Equal(2_000_000, capped.TargetBitrateBps);
        Assert.True(capped.BaseQp > 28, "the QP ramp must track the capped target, not the initial rate");
    }

    /// <summary>While the estimate is depressed the target never exceeds it, on any frame.</summary>
    [Fact]
    public void Target_NeverExceedsEstimate_WhileDepressed()
    {
        var controller = Controller(new RateControlConfig { StabilityWindowFrames = 5 });
        var stats = NormalStats();

        for (var i = 0; i < 50; i++)
        {
            var decision = controller.Decide(Feedback(2_000_000), stats);
            Assert.True(
                decision.TargetBitrateBps <= 2_000_000,
                $"frame {i}: target {decision.TargetBitrateBps} exceeds the 2 Mbps estimate");
        }
    }

    /// <summary>
    /// When the estimate recovers, the ceiling lifts but the target does not jump: it walks back
    /// up through the ordinary stability-window upshift, so a transiently optimistic estimator
    /// cannot spike the encoder above what the path just demonstrated it carries.
    /// </summary>
    [Fact]
    public void EstimateRecovery_ReleasesCeiling_ForGradualUpshift()
    {
        var config = new RateControlConfig { InitialTargetBitrateBps = 8_000_000, StabilityWindowFrames = 5 };
        var controller = Controller(config);
        var stats = NormalStats();

        controller.Decide(Feedback(2_000_000), stats); // collapse: capped to 2 Mbps

        var first = controller.Decide(Feedback(10_000_000), stats);
        Assert.Equal(2_000_000, first.TargetBitrateBps); // no jump on recovery

        EncoderAdaptationDecision last = first;
        for (var i = 0; i < 120; i++)
        {
            last = controller.Decide(Feedback(10_000_000), stats);
        }

        Assert.True(
            last.TargetBitrateBps > 2_000_000,
            $"target must recover above the collapsed level (got {last.TargetBitrateBps})");
        Assert.True(
            last.TargetBitrateBps <= 10_000_000,
            $"target must stay under the recovered estimate (got {last.TargetBitrateBps})");
    }

    /// <summary>Loss keeps driving the target below the estimate — the estimate is a ceiling, not
    /// an authoritative setpoint that would mask congestion the estimator has not seen yet.</summary>
    [Fact]
    public void Loss_StillDownshiftsBelowEstimate()
    {
        var config = new RateControlConfig { InitialTargetBitrateBps = 8_000_000, DownshiftFactor = 0.7 };
        var controller = Controller(config);
        var stats = NormalStats();

        controller.Decide(Feedback(4_000_000), stats); // capped to 4 Mbps
        var decision = controller.Decide(Feedback(4_000_000, loss: 0.05), stats);
        Assert.Equal(2_800_000, decision.TargetBitrateBps); // 4 Mbps * 0.7 — heuristics win below the cap
    }

    /// <summary>An estimate under the configured bitrate floor clamps to the floor: the config's
    /// minimum-quality contract outranks the estimator.</summary>
    [Fact]
    public void EstimateBelowConfiguredFloor_ClampsToFloor()
    {
        var config = new RateControlConfig { MinTargetBitrateBps = 500_000, InitialTargetBitrateBps = 8_000_000 };
        var controller = Controller(config);

        var decision = controller.Decide(Feedback(100_000), NormalStats());
        Assert.Equal(500_000, decision.TargetBitrateBps);
    }

    /// <summary>The 0 sentinel (and any non-positive value) means "no estimate": decisions match a
    /// run that was never given one, frame for frame.</summary>
    [Fact]
    public void NoEstimate_Sentinel_LeavesHeuristicsPathUntouched()
    {
        var stats = NormalStats();

        EncoderAdaptationDecision[] Run(int estimateBps)
        {
            var controller = Controller(new RateControlConfig { StabilityWindowFrames = 5 });
            var losses = new[] { 0.0, 0.0, 0.05, 0.05, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 };
            return losses.Select(loss => controller.Decide(Feedback(estimateBps, loss), stats)).ToArray();
        }

        Assert.Equal(Run(0), Run(-1));
        Assert.Equal(Run(0), Run(int.MaxValue));
    }
}
