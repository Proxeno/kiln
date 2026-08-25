using Microsoft.Extensions.Logging.Abstractions;
using Kiln.RateControl;
using Xunit;

namespace Kiln.Tests.AdaptiveRateControlTests;

/// <summary>
/// Tests for the baseline-relative RTT congestion test
/// (<see cref="RateControlConfig.CongestionRttMultiplier"/> /
/// <see cref="RateControlConfig.CongestionRttFloor"/>). The controller tracks a baseline RTT from
/// the feedback it is fed; congestion requires RTT above both (baseline * multiplier) and the
/// absolute floor — replacing the historical fixed 50 ms test that declared any network with a
/// propagation RTT over 50 ms permanently congested.
/// </summary>
public sealed class RttBaselineTrackingTests
{
    private static EncoderNetworkFeedback FeedbackWithRtt(double rttMs) => new(
        EstimatedAvailableBitrateBps: 10_000_000,
        PacketLossRatio: 0.0,
        RoundTripTime: TimeSpan.FromMilliseconds(rttMs),
        Jitter: TimeSpan.FromMilliseconds(2),
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
        MotionComplexity: 0.0,
        TextureComplexity: 0.0,
        SceneChangeDetected: false);

    private static LowLatencyRateController Controller(RateControlConfig? config = null) =>
        new(config ?? new RateControlConfig(), NullLogger<LowLatencyRateController>.Instance);

    /// <summary>
    /// A link whose propagation RTT is simply high (e.g. 120 ms intercontinental) is not congested:
    /// the baseline tracks 120 ms and 120 &lt; 120 * 2. The historical fixed 50 ms test downshifted
    /// such a link on every single frame.
    /// </summary>
    [Fact]
    public void SteadyHighRtt_IsNotCongestion()
    {
        var controller = Controller();
        var stats = NormalStats();
        var initial = controller.Decide(FeedbackWithRtt(120), stats).TargetBitrateBps;
        for (var i = 0; i < 10; i++)
        {
            var decision = controller.Decide(FeedbackWithRtt(120), stats);
            Assert.True(
                decision.TargetBitrateBps >= initial,
                $"Steady 120 ms RTT must not downshift (frame {i}: {decision.TargetBitrateBps} < {initial})");
        }
    }

    /// <summary>An RTT spike beyond baseline * multiplier is congestion, wherever the baseline sits.</summary>
    [Fact]
    public void SpikeAboveBaselineMultiple_IsCongestion()
    {
        var controller = Controller();
        var stats = NormalStats();

        // Establish a 120 ms baseline, then spike to 300 ms (> 120 * 2).
        var initial = controller.Decide(FeedbackWithRtt(120), stats).TargetBitrateBps;
        var afterSpike = controller.Decide(FeedbackWithRtt(300), stats);
        Assert.True(
            afterSpike.TargetBitrateBps < initial,
            "RTT of 2.5x baseline must downshift bitrate");
    }

    /// <summary>
    /// On a fast link, jitter above the multiplier but at or below the absolute floor is not a
    /// spike: a 5 ms-baseline LAN wobbling to 20 ms clears baseline * 2 but stays under the 50 ms
    /// floor, matching the historical behaviour (RTT ≤ 50 ms never congested).
    /// </summary>
    [Fact]
    public void JitterBelowFloor_IsNotCongestion()
    {
        var controller = Controller();
        var stats = NormalStats();
        var initial = controller.Decide(FeedbackWithRtt(5), stats).TargetBitrateBps;
        var decision = controller.Decide(FeedbackWithRtt(20), stats);
        Assert.True(
            decision.TargetBitrateBps >= initial,
            "20 ms RTT is under the 50 ms floor and must not downshift");
    }

    /// <summary>A spike over both the floor and the baseline multiple on a fast link is congestion.</summary>
    [Fact]
    public void SpikeAboveFloorAndMultiple_IsCongestion()
    {
        var controller = Controller();
        var stats = NormalStats();
        var initial = controller.Decide(FeedbackWithRtt(30), stats).TargetBitrateBps;
        var decision = controller.Decide(FeedbackWithRtt(100), stats);
        Assert.True(
            decision.TargetBitrateBps < initial,
            "100 ms against a 30 ms baseline exceeds both floor and multiplier and must downshift");
    }

    /// <summary>Callers without an RTT measurement (zero) never trip the RTT test.</summary>
    [Fact]
    public void ZeroRtt_NeverCongestion()
    {
        var controller = Controller();
        var stats = NormalStats();
        var initial = controller.Decide(FeedbackWithRtt(0), stats).TargetBitrateBps;
        for (var i = 0; i < 5; i++)
        {
            Assert.True(controller.Decide(FeedbackWithRtt(0), stats).TargetBitrateBps >= initial);
        }
    }

    /// <summary>
    /// A sustained RTT shift (route change) becomes the new baseline: the 1/256-per-decision drift
    /// lets baseline*multiplier overtake the new level, congestion clears, and after a stability
    /// window the bitrate recovers off its floor instead of staying pinned there forever.
    /// </summary>
    [Fact]
    public void SustainedElevatedRtt_BecomesNewBaseline_AndRecovers()
    {
        var config = new RateControlConfig();
        var controller = Controller(config);
        var stats = NormalStats();

        // 30 ms baseline, then a permanent move to 100 ms.
        controller.Decide(FeedbackWithRtt(30), stats);
        EncoderAdaptationDecision last = null!;
        for (var i = 0; i < 300; i++)
        {
            last = controller.Decide(FeedbackWithRtt(100), stats);
        }

        Assert.True(
            last.TargetBitrateBps > config.MinTargetBitrateBps,
            $"After ~90 decisions the 100 ms level is the baseline; 300 decisions later the bitrate " +
            $"must have recovered above the floor (got {last.TargetBitrateBps})");
    }

    /// <summary>Identical feedback sequences produce identical decision sequences (determinism).</summary>
    [Fact]
    public void BaselineTracking_IsDeterministic()
    {
        var rtts = new double[] { 30, 32, 100, 100, 45, 30, 200, 30, 30, 90 };

        EncoderAdaptationDecision[] Run()
        {
            var controller = Controller();
            var stats = NormalStats();
            return rtts.Select(rtt => controller.Decide(FeedbackWithRtt(rtt), stats)).ToArray();
        }

        Assert.Equal(Run(), Run());
    }
}
