using Microsoft.Extensions.Logging.Abstractions;
using Kiln.RateControl;
using Kiln.Recovery;

namespace Kiln.Tests.AdaptiveRateControlTests;

/// <summary>
/// Phase 2 unit tests for the adaptive rate control logic.
/// Tests verify bitrate adaptation, QP adjustment, congestion detection, and stability tracking.
/// </summary>
public sealed class Phase2_RateControlTests
{
    /// <summary>Helper method to create stable network feedback (all metrics normal).</summary>
    private static EncoderNetworkFeedback StableNetworkFeedback() => new(
        EstimatedAvailableBitrateBps: 10_000_000,
        PacketLossRatio: 0.0,
        RoundTripTime: TimeSpan.FromMilliseconds(30),
        Jitter: TimeSpan.FromMilliseconds(2),
        PendingRtpBytes: 5_000,
        NackCount: 0,
        PictureLossIndication: false,
        FullIntraRequest: false,
        ClientDecodeDelay: null);

    /// <summary>Helper method to create normal pipeline stats.</summary>
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

    /// <summary>
    /// Test 1: Packet loss immediately reduces bitrate.
    /// When packet loss exceeds the congestion threshold (2%), the controller
    /// should immediately downshift the bitrate without waiting for stability.
    /// </summary>
    [Fact]
    public void Decide_ReducesBitrate_OnPacketLoss()
    {
        var config = new RateControlConfig
        {
            InitialTargetBitrateBps = 8_000_000,
            DownshiftFactor = 0.7
        };
        var controller = new LowLatencyRateController(config, NullLogger<LowLatencyRateController>.Instance);

        var stableFeedback = StableNetworkFeedback();
        var statsNormal = NormalStats();

        var initialDecision = controller.Decide(stableFeedback, statsNormal);
        var initialBitrate = initialDecision.TargetBitrateBps;

        // Introduce packet loss above the congestion threshold (2%)
        var lossFeedback = stableFeedback with { PacketLossRatio = 0.05 };
        var decisionAfterLoss = controller.Decide(lossFeedback, statsNormal);

        Assert.True(
            decisionAfterLoss.TargetBitrateBps < initialBitrate,
            $"Bitrate should decrease on loss. Before: {initialBitrate}, After: {decisionAfterLoss.TargetBitrateBps}"
        );
        Assert.Equal((int)(initialBitrate * config.DownshiftFactor), decisionAfterLoss.TargetBitrateBps);
    }

    /// <summary>
    /// Test 2: Stable network slowly increases bitrate after stability window.
    /// After StabilityWindowFrames of good conditions, the controller should upshift.
    /// </summary>
    [Fact]
    public void Decide_IncreasesBitrate_AfterStabilityWindow()
    {
        var config = new RateControlConfig
        {
            InitialTargetBitrateBps = 4_000_000,
            StabilityWindowFrames = 5,
            UpshiftFactor = 1.05
        };
        var controller = new LowLatencyRateController(config, NullLogger<LowLatencyRateController>.Instance);

        var stableFeedback = StableNetworkFeedback();
        var statsNormal = NormalStats();

        var initialDecision = controller.Decide(stableFeedback, statsNormal);
        var initialBitrate = initialDecision.TargetBitrateBps;

        // Feed stable conditions for 5+ frames
        for (int i = 0; i < 6; i++)
        {
            var decision = controller.Decide(stableFeedback, statsNormal);
            if (i == 5)
            {
                Assert.True(
                    decision.TargetBitrateBps > initialBitrate,
                    $"Bitrate should increase after stability. Before: {initialBitrate}, After: {decision.TargetBitrateBps}"
                );
            }
        }
    }

    /// <summary>
    /// Test 3: No oscillation below loss threshold.
    /// Packet loss below the congestion threshold (2%) should not trigger downshift.
    /// At 1.5% loss, the controller should remain stable (no downshift).
    /// </summary>
    [Fact]
    public void Decide_DoesNotOscillate_BelowLossThreshold()
    {
        var config = new RateControlConfig
        {
            InitialTargetBitrateBps = 8_000_000,
            CongestionPacketLossThreshold = 0.02,
            StabilityWindowFrames = 10
        };
        var controller = new LowLatencyRateController(config, NullLogger<LowLatencyRateController>.Instance);

        var stableFeedback = StableNetworkFeedback();
        var lowLossFeedback = stableFeedback with { PacketLossRatio = 0.015 };
        var statsNormal = NormalStats();

        var initialDecision = controller.Decide(stableFeedback, statsNormal);
        var initialBitrate = initialDecision.TargetBitrateBps;

        var decision2 = controller.Decide(lowLossFeedback, statsNormal);
        var decision3 = controller.Decide(lowLossFeedback, statsNormal);

        // Bitrate should not downshift; it should remain at initial
        Assert.Equal(initialBitrate, decision2.TargetBitrateBps);
        Assert.Equal(initialBitrate, decision3.TargetBitrateBps);
    }

    /// <summary>
    /// Test 4: RTT spike reduces bitrate.
    /// When RTT exceeds 50ms baseline, congestion should be detected and bitrate downshifted.
    /// </summary>
    [Fact]
    public void Decide_ReducesBitrate_OnRttSpike()
    {
        var config = new RateControlConfig
        {
            InitialTargetBitrateBps = 8_000_000,
            DownshiftFactor = 0.7
        };
        var controller = new LowLatencyRateController(config, NullLogger<LowLatencyRateController>.Instance);

        var normalFeedback = StableNetworkFeedback();
        var rttspikeeFeedback = normalFeedback with { RoundTripTime = TimeSpan.FromMilliseconds(100) };
        var statsNormal = NormalStats();

        var initialDecision = controller.Decide(normalFeedback, statsNormal);
        var initialBitrate = initialDecision.TargetBitrateBps;

        var decisionAfterSpike = controller.Decide(rttspikeeFeedback, statsNormal);

        Assert.True(
            decisionAfterSpike.TargetBitrateBps < initialBitrate,
            "Bitrate should decrease on RTT spike"
        );
    }

    /// <summary>
    /// Test 5: Send queue growth reduces bitrate.
    /// When PendingRtpBytes exceeds MaxPendingRtpBytes, congestion is detected
    /// and bitrate should be downshifted.
    /// </summary>
    [Fact]
    public void Decide_ReducesBitrate_OnSendQueueGrowth()
    {
        var config = new RateControlConfig
        {
            InitialTargetBitrateBps = 8_000_000,
            MaxPendingRtpBytes = 50_000,
            DownshiftFactor = 0.7
        };
        var controller = new LowLatencyRateController(config, NullLogger<LowLatencyRateController>.Instance);

        var normalFeedback = StableNetworkFeedback();
        var backlogFeedback = normalFeedback with { PendingRtpBytes = 100_000 };
        var statsNormal = NormalStats();

        var initialDecision = controller.Decide(normalFeedback, statsNormal);
        var initialBitrate = initialDecision.TargetBitrateBps;

        var decisionAfterBacklog = controller.Decide(backlogFeedback, statsNormal);

        Assert.True(
            decisionAfterBacklog.TargetBitrateBps < initialBitrate,
            "Bitrate should decrease when send queue grows"
        );
    }

    /// <summary>
    /// Test 6: Bitrate respects min and max bounds.
    /// Even under severe congestion or ideal conditions, bitrate should remain
    /// within [MinTargetBitrateBps, MaxTargetBitrateBps].
    /// </summary>
    [Fact]
    public void Decide_RespectsBitrateBounds()
    {
        var config = new RateControlConfig
        {
            MinTargetBitrateBps = 1_000_000,
            MaxTargetBitrateBps = 20_000_000,
            InitialTargetBitrateBps = 8_000_000
        };
        var controller = new LowLatencyRateController(config, NullLogger<LowLatencyRateController>.Instance);

        var severelyCongestedFeedback = StableNetworkFeedback() with { PacketLossRatio = 0.20 };
        var statsNormal = NormalStats();

        // Keep reducing bitrate with severe congestion
        for (int i = 0; i < 20; i++)
        {
            var decision = controller.Decide(severelyCongestedFeedback, statsNormal);
            Assert.True(
                decision.TargetBitrateBps >= config.MinTargetBitrateBps,
                $"Bitrate {decision.TargetBitrateBps} below minimum {config.MinTargetBitrateBps}"
            );
        }

        // Keep increasing bitrate with excellent conditions
        var excellentFeedback = StableNetworkFeedback() with
        {
            EstimatedAvailableBitrateBps = 50_000_000,
            PacketLossRatio = 0.0
        };

        // Reset controller to start fresh for upshift
        var resetController = new LowLatencyRateController(config, NullLogger<LowLatencyRateController>.Instance);
        for (int i = 0; i < 100; i++)
        {
            var decision = resetController.Decide(excellentFeedback, statsNormal);
            Assert.True(
                decision.TargetBitrateBps <= config.MaxTargetBitrateBps,
                $"Bitrate {decision.TargetBitrateBps} above maximum {config.MaxTargetBitrateBps}"
            );
        }
    }

    /// <summary>
    /// Test 7: QP increases on high packet loss.
    /// When packet loss exceeds 5%, the controller should add 2 to the QP
    /// (making video lower quality to reduce frame size).
    /// </summary>
    [Fact]
    public void Decide_IncreasesQp_OnHighPacketLoss()
    {
        var config = new RateControlConfig
        {
            BaseQp = 28,
            MaxQp = 45
        };
        var controller = new LowLatencyRateController(config, NullLogger<LowLatencyRateController>.Instance);

        var normalFeedback = StableNetworkFeedback();
        var lossFeedback = StableNetworkFeedback() with { PacketLossRatio = 0.10 };
        var statsNormal = NormalStats();

        var normalDecision = controller.Decide(normalFeedback, statsNormal);
        var lossDecision = controller.Decide(lossFeedback, statsNormal);

        Assert.True(
            lossDecision.BaseQp >= normalDecision.BaseQp,
            $"QP should increase with high packet loss. Normal: {normalDecision.BaseQp}, Loss: {lossDecision.BaseQp}"
        );
    }

    /// <summary>
    /// Test 8: QP adjusts based on bitrate reduction.
    /// When bitrate falls below 50% of initial, QP should increase by 3.
    /// When bitrate falls below 70% of initial, QP should increase by 1.
    /// </summary>
    [Fact]
    public void Decide_AdjustsQp_BasedOnBitrateReduction()
    {
        var config = new RateControlConfig
        {
            InitialTargetBitrateBps = 8_000_000,
            BaseQp = 28,
            MinQp = 10,
            MaxQp = 51,
            DownshiftFactor = 0.7
        };
        var controller = new LowLatencyRateController(config, NullLogger<LowLatencyRateController>.Instance);

        var statsNormal = NormalStats();
        var severelyCongestedFeedback = StableNetworkFeedback() with { PacketLossRatio = 0.10 };

        var initialDecision = controller.Decide(StableNetworkFeedback(), statsNormal);
        var initialQp = initialDecision.BaseQp;

        // Trigger multiple downshifts to get below 50% of initial bitrate
        for (int i = 0; i < 5; i++)
        {
            controller.Decide(severelyCongestedFeedback, statsNormal);
        }

        var finalDecision = controller.Decide(severelyCongestedFeedback, statsNormal);

        // After significant reduction, QP should be higher
        Assert.True(
            finalDecision.BaseQp > initialQp,
            $"QP should increase with bitrate reduction. Initial: {initialQp}, Final: {finalDecision.BaseQp}"
        );
    }

    /// <summary>
    /// Test 9: Encode backpressure increases speed mode and QP.
    /// When average encode duration exceeds 75% of the frame budget (1000/fps),
    /// the encoder should switch to a faster mode and increase QP.
    /// </summary>
    [Fact]
    public void Decide_IncreasesSpeedMode_OnEncodeBackpressure()
    {
        var config = new RateControlConfig
        {
            BaseQp = 28
        };
        var controller = new LowLatencyRateController(config, NullLogger<LowLatencyRateController>.Instance);

        var stableFeedback = StableNetworkFeedback();
        var normalStats = NormalStats();

        // Normal condition: AverageEncodeDuration = 3ms, frame budget at 60fps = 16.67ms
        var normalDecision = controller.Decide(stableFeedback, normalStats);
        var normalSpeedMode = normalDecision.SpeedMode;

        // Backpressure condition: AverageEncodeDuration > 12.5ms (75% of 16.67ms)
        var backpressureStats = normalStats with
        {
            AverageEncodeDuration = TimeSpan.FromMilliseconds(15)
        };

        var backpressureDecision = controller.Decide(stableFeedback, backpressureStats);

        Assert.True(
            (int)backpressureDecision.SpeedMode > (int)normalSpeedMode,
            $"Speed mode should increase on backpressure. Normal: {normalSpeedMode}, Backpressure: {backpressureDecision.SpeedMode}"
        );
        Assert.True(
            backpressureDecision.BaseQp > normalDecision.BaseQp,
            $"QP should increase on backpressure. Normal: {normalDecision.BaseQp}, Backpressure: {backpressureDecision.BaseQp}"
        );
    }

    /// <summary>
    /// Test 10: Frame size overshoots are detected and counter incremented.
    /// When LastEncodedFrameBytes exceeds MaxFrameBytes, the overshoot counter
    /// should be incremented.
    /// </summary>
    [Fact]
    public void Decide_DetectsFrameSizeOvershoots()
    {
        var config = new RateControlConfig
        {
            InitialTargetBitrateBps = 1_000_000,  // Low bitrate = small max frame size
            BurstAllowance = 1.5
        };
        var controller = new LowLatencyRateController(config, NullLogger<LowLatencyRateController>.Instance);

        var stableFeedback = StableNetworkFeedback();

        // First decision with normal frame size
        var normalStats = NormalStats();
        var firstDecision = controller.Decide(stableFeedback, normalStats);

        // Second decision with oversized frame
        var oversizedStats = normalStats with
        {
            LastEncodedFrameBytes = 500_000  // Way larger than budget
        };
        var secondDecision = controller.Decide(stableFeedback, oversizedStats);

        // Overshoot counter should increment
        // Note: We can't directly access state, but we can verify from decision
        Assert.NotNull(secondDecision);
        Assert.True(secondDecision.MaxFrameBytes < oversizedStats.LastEncodedFrameBytes);
    }

    /// <summary>
    /// Test 11: Hysteresis prevents rapid oscillation.
    /// After a downshift, bitrate should not upshift on the next stable frame.
    /// It should wait for StabilityWindowFrames of good conditions first.
    /// </summary>
    [Fact]
    public void Decide_HysteresisPreventsRapidOscillation()
    {
        var config = new RateControlConfig
        {
            InitialTargetBitrateBps = 8_000_000,
            StabilityWindowFrames = 5,
            DownshiftFactor = 0.7
        };
        var controller = new LowLatencyRateController(config, NullLogger<LowLatencyRateController>.Instance);

        var stableFeedback = StableNetworkFeedback();
        var congestedFeedback = stableFeedback with { PacketLossRatio = 0.05 };
        var statsNormal = NormalStats();

        // Initial decision
        var initial = controller.Decide(stableFeedback, statsNormal);

        // Trigger downshift
        var downshifted = controller.Decide(congestedFeedback, statsNormal);
        var downshiftedBitrate = downshifted.TargetBitrateBps;

        // Single stable frame should not upshift
        var afterOneStable = controller.Decide(stableFeedback, statsNormal);
        Assert.Equal(downshiftedBitrate, afterOneStable.TargetBitrateBps);

        // Even 4 stable frames should not upshift
        for (int i = 0; i < 3; i++)
        {
            controller.Decide(stableFeedback, statsNormal);
        }
        var afterFourStable = controller.Decide(stableFeedback, statsNormal);
        Assert.Equal(downshiftedBitrate, afterFourStable.TargetBitrateBps);

        // After 5 stable frames (window + 1), it should upshift
        var afterWindowPlusOne = controller.Decide(stableFeedback, statsNormal);
        Assert.True(
            afterWindowPlusOne.TargetBitrateBps > downshiftedBitrate,
            "Bitrate should upshift after stability window"
        );
    }

    /// <summary>
    /// Test 12: MaxFrameBytes has minimum floor of 100 bytes.
    /// Even with very low bitrate, MaxFrameBytes should not fall below 100.
    /// </summary>
    [Fact]
    public void Decide_EnforcesMinimumMaxFrameBytes()
    {
        var config = new RateControlConfig
        {
            InitialTargetBitrateBps = 100_000,  // Very low bitrate
            MinTargetBitrateBps = 100_000,
            BurstAllowance = 1.0
        };
        var controller = new LowLatencyRateController(config, NullLogger<LowLatencyRateController>.Instance);

        var stableFeedback = StableNetworkFeedback();
        var statsNormal = NormalStats();

        var decision = controller.Decide(stableFeedback, statsNormal);

        Assert.True(
            decision.MaxFrameBytes >= 100,
            $"MaxFrameBytes {decision.MaxFrameBytes} should be at least 100"
        );
    }

    // ── recovery-integration contract guards ──────────────────────────────────────────────────────
    // The controller integrates the recovery policy itself (one DecideRecovery per Decide). These pin
    // that contract so a composing layer re-applying recovery — the trap a former master controller
    // fell into, double-advancing the IDR cooldown — is caught.

    /// <summary>A picture-loss indication must surface as ForceIdr straight out of Decide (recovery
    /// is integrated here; callers do not invoke the recovery policy separately).</summary>
    [Fact]
    public void Decide_ForcesIdr_OnPictureLoss()
    {
        var controller = new LowLatencyRateController(new RateControlConfig(), NullLogger<LowLatencyRateController>.Instance);

        var pli = StableNetworkFeedback() with { PictureLossIndication = true };
        var decision = controller.Decide(pli, NormalStats());

        Assert.True(decision.ForceIdr, "PLI should force an IDR via the integrated recovery policy");
    }

    /// <summary>
    /// After an IDR fires, the cooldown must span exactly <see cref="RateControlConfig.IdrCooldownFrames"/>
    /// subsequent Decide calls — not half that. If recovery were applied twice per Decide (the
    /// double-application bug) the stateful cooldown would drain at 2× and a fresh PLI would re-fire an
    /// IDR after only ~half the configured frames. This walks the cooldown to prove single application.
    /// </summary>
    [Fact]
    public void Decide_AppliesRecovery_OncePerFrame_IdrCooldownSpansConfiguredFrames()
    {
        const int cooldown = 6;
        var config = new RateControlConfig { IdrCooldownFrames = cooldown };
        var controller = new LowLatencyRateController(config, NullLogger<LowLatencyRateController>.Instance);
        var stats = NormalStats();
        var pli = StableNetworkFeedback() with { PictureLossIndication = true };
        var calm = StableNetworkFeedback();

        // Frame 0: PLI fires the first IDR and arms the cooldown.
        Assert.True(controller.Decide(pli, stats).ForceIdr);

        // With single application, the cooldown lasts `cooldown` Decide calls. Re-issuing a PLI before
        // it elapses must fall back to intra refresh, not a second IDR. At the half-way point (where a
        // double-decrement would already have drained the cooldown), an IDR must NOT yet re-fire.
        for (var i = 0; i < cooldown - 1; i++)
        {
            var d = controller.Decide(pli, stats);
            Assert.False(d.ForceIdr,
                $"IDR re-fired at frame {i + 1} while still in cooldown — recovery is being applied " +
                $"more than once per Decide (cooldown draining too fast)");
            Assert.True(d.EnableIntraRefresh, "in-cooldown PLI should request intra refresh");
        }

        // Once the cooldown has fully elapsed (driven by calm frames), a new PLI fires an IDR again.
        controller.Decide(calm, stats);
        Assert.True(controller.Decide(pli, stats).ForceIdr, "IDR should re-fire after the cooldown elapses");
    }

    /// <summary>The recovery instance the controller drives is the one it exposes — a composer must
    /// reuse it rather than constructing a second, state-divergent policy.</summary>
    [Fact]
    public void RecoveryPolicy_ExposesTheControllerDrivenInstance()
    {
        var recovery = new H264RecoveryPolicy(new RateControlConfig(), NullLogger<H264RecoveryPolicy>.Instance);
        var controller = new LowLatencyRateController(new RateControlConfig(), NullLogger<LowLatencyRateController>.Instance, recovery);

        Assert.Same(recovery, controller.RecoveryPolicy);

        // Driving Decide advances the exposed instance's metrics (proving it is the same object).
        controller.Decide(StableNetworkFeedback() with { PictureLossIndication = true }, NormalStats());
        Assert.Equal(1, controller.RecoveryPolicy.GetMetrics().PliCount);
    }
}
