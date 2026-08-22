using Microsoft.Extensions.Logging.Abstractions;
using Kiln.RateControl;

namespace Kiln.Tests.AdaptiveRateControlTests;

/// <summary>
/// Phase 1 unit tests for the adaptive rate control infrastructure.
/// Tests verify that core types, configuration, and the rate controller stub work correctly.
/// </summary>
public sealed class Phase1_InterfacesTests
{
    /// <summary>
    /// Test 1: Verify that EncoderNetworkFeedback and EncoderPipelineStats
    /// can be instantiated with realistic cloud-gaming data.
    /// </summary>
    [Fact]
    public void EncoderNetworkFeedback_CanBeInstantiatedWithRealisticData()
    {
        var feedback = new EncoderNetworkFeedback(
            EstimatedAvailableBitrateBps: 8_000_000,
            PacketLossRatio: 0.01,
            RoundTripTime: TimeSpan.FromMilliseconds(50),
            Jitter: TimeSpan.FromMilliseconds(5),
            PendingRtpBytes: 10_000,
            NackCount: 5,
            PictureLossIndication: false,
            FullIntraRequest: false,
            ClientDecodeDelay: TimeSpan.FromMilliseconds(100)
        );

        Assert.NotNull(feedback);
        Assert.Equal(8_000_000, feedback.EstimatedAvailableBitrateBps);
        Assert.Equal(0.01, feedback.PacketLossRatio);
        Assert.Equal(TimeSpan.FromMilliseconds(50), feedback.RoundTripTime);
        Assert.Equal(10_000, feedback.PendingRtpBytes);
        Assert.Equal(5, feedback.NackCount);
        Assert.False(feedback.PictureLossIndication);
        Assert.False(feedback.FullIntraRequest);
        Assert.Equal(TimeSpan.FromMilliseconds(100), feedback.ClientDecodeDelay);

        var stats = new EncoderPipelineStats(
            LastEncodeDuration: TimeSpan.FromMilliseconds(10),
            AverageEncodeDuration: TimeSpan.FromMilliseconds(9),
            PendingInputFrames: 2,
            PendingEncodedFrames: 1,
            DroppedInputFrames: 0,
            DroppedEncodedFrames: 0,
            LastEncodedFrameBytes: 15_000,
            LastFrameQp: 28,
            LastFrameWasIdr: false,
            MotionComplexity: 0.5,
            TextureComplexity: 0.4,
            SceneChangeDetected: false
        );

        Assert.NotNull(stats);
        Assert.Equal(TimeSpan.FromMilliseconds(10), stats.LastEncodeDuration);
        Assert.Equal(2, stats.PendingInputFrames);
        Assert.Equal(15_000, stats.LastEncodedFrameBytes);
        Assert.Equal(28, stats.LastFrameQp);
        Assert.False(stats.LastFrameWasIdr);
        Assert.Equal(0.5, stats.MotionComplexity);
        Assert.Equal(0.4, stats.TextureComplexity);
        Assert.False(stats.SceneChangeDetected);
    }

    /// <summary>
    /// Test 2: Verify that the rate controller produces deterministic decisions
    /// when given the same feedback and stats (same inputs → same outputs).
    /// </summary>
    [Fact]
    public void RateController_ProducesDeterministicDecisions_GivenSameFeedback()
    {
        var config = new RateControlConfig();
        var controller = new LowLatencyRateController(config, NullLogger<LowLatencyRateController>.Instance);

        var feedback = new EncoderNetworkFeedback(
            EstimatedAvailableBitrateBps: 8_000_000,
            PacketLossRatio: 0.01,
            RoundTripTime: TimeSpan.FromMilliseconds(50),
            Jitter: TimeSpan.FromMilliseconds(5),
            PendingRtpBytes: 10_000,
            NackCount: 5,
            PictureLossIndication: false,
            FullIntraRequest: false,
            ClientDecodeDelay: TimeSpan.FromMilliseconds(100)
        );

        var stats = new EncoderPipelineStats(
            LastEncodeDuration: TimeSpan.FromMilliseconds(10),
            AverageEncodeDuration: TimeSpan.FromMilliseconds(9),
            PendingInputFrames: 2,
            PendingEncodedFrames: 1,
            DroppedInputFrames: 0,
            DroppedEncodedFrames: 0,
            LastEncodedFrameBytes: 15_000,
            LastFrameQp: 28,
            LastFrameWasIdr: false,
            MotionComplexity: 0.5,
            TextureComplexity: 0.4,
            SceneChangeDetected: false
        );

        var decision1 = controller.Decide(feedback, stats);
        var decision2 = controller.Decide(feedback, stats);

        Assert.Equal(decision1, decision2);
        Assert.NotNull(decision1);
        Assert.NotNull(decision2);
    }

    /// <summary>
    /// Test 3: Verify that MaxFrameBytes is calculated correctly
    /// using the formula: (bitrate / 8 / fps) * burstAllowance.
    /// </summary>
    [Fact]
    public void MaxFrameBytes_CalculatedCorrectly()
    {
        var config = new RateControlConfig
        {
            InitialTargetBitrateBps = 8_000_000,
            BurstAllowance = 2.0
        };
        var controller = new LowLatencyRateController(config, NullLogger<LowLatencyRateController>.Instance);

        var feedback = new EncoderNetworkFeedback(
            EstimatedAvailableBitrateBps: 8_000_000,
            PacketLossRatio: 0.01,
            RoundTripTime: TimeSpan.FromMilliseconds(50),
            Jitter: TimeSpan.FromMilliseconds(5),
            PendingRtpBytes: 10_000,
            NackCount: 0,
            PictureLossIndication: false,
            FullIntraRequest: false,
            ClientDecodeDelay: null
        );

        var stats = new EncoderPipelineStats(
            LastEncodeDuration: TimeSpan.FromMilliseconds(10),
            AverageEncodeDuration: TimeSpan.FromMilliseconds(9),
            PendingInputFrames: 0,
            PendingEncodedFrames: 0,
            DroppedInputFrames: 0,
            DroppedEncodedFrames: 0,
            LastEncodedFrameBytes: 15_000,
            LastFrameQp: 28,
            LastFrameWasIdr: false,
            MotionComplexity: 0.0,
            TextureComplexity: 0.0,
            SceneChangeDetected: false
        );

        var decision = controller.Decide(feedback, stats);

        // 8,000,000 bps / 8 bits / 60 fps = ~16,667 bytes/frame
        // maxFrameBytes = 16,667 * 2.0 = 33,334
        Assert.True(decision.MaxFrameBytes >= 33_000 && decision.MaxFrameBytes <= 34_000,
            $"MaxFrameBytes {decision.MaxFrameBytes} is not in expected range [33000, 34000]");
    }

    /// <summary>
    /// Test 4: Verify that decision respects configuration bounds
    /// (bitrate, QP, resolution, and frame rate within configured limits).
    /// </summary>
    [Fact]
    public void Decision_RespectsConfigBounds()
    {
        var config = new RateControlConfig
        {
            MinTargetBitrateBps = 1_000_000,
            MaxTargetBitrateBps = 20_000_000,
            MinQp = 15,
            MaxQp = 45,
            InitialTargetBitrateBps = 8_000_000
        };
        var controller = new LowLatencyRateController(config, NullLogger<LowLatencyRateController>.Instance);

        var feedback = new EncoderNetworkFeedback(
            EstimatedAvailableBitrateBps: 8_000_000,
            PacketLossRatio: 0.01,
            RoundTripTime: TimeSpan.FromMilliseconds(50),
            Jitter: TimeSpan.FromMilliseconds(5),
            PendingRtpBytes: 10_000,
            NackCount: 0,
            PictureLossIndication: false,
            FullIntraRequest: false,
            ClientDecodeDelay: null
        );

        var stats = new EncoderPipelineStats(
            LastEncodeDuration: TimeSpan.FromMilliseconds(10),
            AverageEncodeDuration: TimeSpan.FromMilliseconds(9),
            PendingInputFrames: 0,
            PendingEncodedFrames: 0,
            DroppedInputFrames: 0,
            DroppedEncodedFrames: 0,
            LastEncodedFrameBytes: 15_000,
            LastFrameQp: 28,
            LastFrameWasIdr: false,
            MotionComplexity: 0.0,
            TextureComplexity: 0.0,
            SceneChangeDetected: false
        );

        var decision = controller.Decide(feedback, stats);

        Assert.NotNull(decision);
        Assert.True(decision.TargetBitrateBps >= config.MinTargetBitrateBps,
            $"Bitrate {decision.TargetBitrateBps} is below minimum {config.MinTargetBitrateBps}");
        Assert.True(decision.TargetBitrateBps <= config.MaxTargetBitrateBps,
            $"Bitrate {decision.TargetBitrateBps} exceeds maximum {config.MaxTargetBitrateBps}");
        Assert.True(decision.BaseQp >= config.MinQp,
            $"QP {decision.BaseQp} is below minimum {config.MinQp}");
        Assert.True(decision.BaseQp <= config.MaxQp,
            $"QP {decision.BaseQp} exceeds maximum {config.MaxQp}");
        Assert.True(decision.TargetFps > 0 && decision.TargetFps <= 120,
            $"FPS {decision.TargetFps} is not in valid range (1-120)");
        Assert.True(decision.Width > 0 && decision.Height > 0,
            $"Resolution {decision.Width}x{decision.Height} is invalid");
    }
}
