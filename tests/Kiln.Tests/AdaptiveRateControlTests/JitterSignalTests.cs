using Microsoft.Extensions.Logging.Abstractions;
using Kiln.Internal.H264.Adaptation;
using Kiln.RateControl;
using Xunit;

namespace Kiln.Tests.AdaptiveRateControlTests;

/// <summary>
/// Tests for <see cref="EncoderNetworkFeedback.Jitter"/> as a queueing early warning
/// (<see cref="RateControlConfig.JitterSpikeMultiplier"/> /
/// <see cref="RateControlConfig.JitterSpikeFloor"/>): rising jitter without loss means queues are
/// building — the case where backing off aggressively is exactly wrong — so a spike tempers
/// increases (bitrate upshift holds, ladder walk-up defers) and never cuts anything.
/// </summary>
public sealed class JitterSignalTests
{
    private static EncoderNetworkFeedback Feedback(double jitterMs, double loss = 0.0) => new(
        EstimatedAvailableBitrateBps: 0,
        PacketLossRatio: loss,
        RoundTripTime: TimeSpan.FromMilliseconds(30),
        Jitter: TimeSpan.FromMilliseconds(jitterMs),
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
        new(config ?? new RateControlConfig { StabilityWindowFrames = 5 },
            NullLogger<LowLatencyRateController>.Instance);

    /// <summary>
    /// The direction test: identical loss-free runs, one with calm jitter and one whose jitter
    /// rises to 30 ms, must diverge — the calm run upshifts after the stability window, the
    /// spiking run holds its bitrate (and never cuts it).
    /// </summary>
    [Fact]
    public void RisingJitterWithoutLoss_HoldsUpshift_CalmRunUpshifts()
    {
        var stats = NormalStats();

        var calm = Controller();
        var initial = calm.Decide(Feedback(2), stats).TargetBitrateBps;
        for (var i = 0; i < 20; i++)
        {
            calm.Decide(Feedback(2), stats);
        }

        Assert.True(
            calm.Decide(Feedback(2), stats).TargetBitrateBps > initial,
            "the calm run must upshift after the stability window");

        var spiking = Controller();
        spiking.Decide(Feedback(2), stats); // baseline 2 ms
        for (var i = 0; i < 20; i++)
        {
            var decision = spiking.Decide(Feedback(30), stats);
            Assert.Equal(initial, decision.TargetBitrateBps); // held: no upshift, and no cut either
        }
    }

    /// <summary>Once jitter settles, the upshift resumes — the hold froze the stability count
    /// rather than resetting it, so recovery is prompt.</summary>
    [Fact]
    public void UpshiftResumes_WhenJitterSettles()
    {
        var controller = Controller();
        var stats = NormalStats();

        var initial = controller.Decide(Feedback(2), stats).TargetBitrateBps;
        for (var i = 0; i < 10; i++)
        {
            controller.Decide(Feedback(30), stats); // held
        }

        EncoderAdaptationDecision last = null!;
        for (var i = 0; i < 7; i++)
        {
            last = controller.Decide(Feedback(2), stats);
        }

        Assert.True(
            last.TargetBitrateBps > initial,
            $"upshift must resume once jitter settles (got {last.TargetBitrateBps})");
    }

    /// <summary>
    /// A link whose jitter is simply high (steady 30 ms — e.g. Wi-Fi) is its own baseline and is
    /// not held below capacity: the spike test is baseline-relative, exactly like the RTT tests,
    /// so only a rise above the link's norm tempers the upshift.
    /// </summary>
    [Fact]
    public void SteadyHighJitter_IsBaseline_DoesNotHoldUpshift()
    {
        var controller = Controller();
        var stats = NormalStats();

        var initial = controller.Decide(Feedback(30), stats).TargetBitrateBps;
        EncoderAdaptationDecision last = null!;
        for (var i = 0; i < 21; i++)
        {
            last = controller.Decide(Feedback(30), stats);
        }

        Assert.True(last.TargetBitrateBps > initial, "steady high jitter must not hold the upshift");
    }

    /// <summary>Jitter under the absolute floor never spikes, however large the ratio to the
    /// baseline: a 1 ms link wobbling to 8 ms is ordinary, not queueing.</summary>
    [Fact]
    public void JitterBelowFloor_NeverSpikes()
    {
        var stats = NormalStats();

        EncoderAdaptationDecision[] Run(double spikeMs)
        {
            var controller = Controller();
            var decisions = new List<EncoderAdaptationDecision> { controller.Decide(Feedback(1), stats) };
            for (var i = 0; i < 20; i++)
            {
                decisions.Add(controller.Decide(Feedback(spikeMs), stats));
            }

            return [.. decisions];
        }

        Assert.Equal(Run(1), Run(8)); // 8x the baseline but under the 10 ms floor: identical decisions
    }

    /// <summary>Zero jitter — callers without a measurement, the record default — never spikes and
    /// never moves the baseline.</summary>
    [Fact]
    public void ZeroJitter_HasNoEffect()
    {
        var controller = Controller();
        var stats = NormalStats();

        var initial = controller.Decide(Feedback(0), stats).TargetBitrateBps;
        EncoderAdaptationDecision last = null!;
        for (var i = 0; i < 21; i++)
        {
            last = controller.Decide(Feedback(0), stats);
        }

        Assert.True(last.TargetBitrateBps > initial, "zero jitter must leave the upshift path untouched");
    }

    /// <summary>A jitter spike also defers the ladder walk-up: raising resolution into a building
    /// queue would feed the congestion the early warning predicts. It clears when jitter does.</summary>
    [Fact]
    public void JitterSpike_DefersLadderWalkUp()
    {
        var policy = new AdaptationPolicy(
            new RateControlConfig(), new ResolutionLadder(), new FpsLadder(),
            NullLogger<AdaptationPolicy>.Instance);
        var stats = NormalStats() with { MotionComplexity = 0.2, TextureComplexity = 0.3 };
        var atTop = new EncoderAdaptationDecision(
            TargetBitrateBps: 8_000_000, MaxFrameBytes: 30_000, TargetFps: 60, Width: 1920, Height: 1080,
            BaseQp: 28, ForceIdr: false, EnableIntraRefresh: false, SpeedMode: EncoderSpeedMode.HighQuality);
        var degraded = atTop with { Width = 1280, Height = 720 };

        policy.DecideAdaptation(Feedback(2), stats, atTop); // baseline 2 ms; nothing to walk up, no cooldown

        var held = policy.DecideAdaptation(Feedback(30), stats, degraded);
        Assert.Equal(720, held.Height);
        Assert.Equal("", held.Reason);

        var recovered = policy.DecideAdaptation(Feedback(2), stats, degraded);
        Assert.Equal("resolution_increase_stable", recovered.Reason);
        Assert.Equal(900, recovered.Height);
    }
}
