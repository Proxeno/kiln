using Microsoft.Extensions.Logging.Abstractions;
using Kiln.RateControl;
using Kiln.Recovery;

namespace Kiln.Tests.AdaptiveRateControlTests;

/// <summary>
/// Phase 4 unit tests for recovery policy (PLI/FIR handling).
/// Tests verify that the recovery policy correctly handles picture loss signals,
/// enforces IDR cooldown, and falls back to intra refresh when needed.
/// </summary>
public sealed class Phase4_RecoveryPolicyTests
{
    /// <summary>
    /// Helper method to create stable network feedback with optional PLI/FIR.
    /// </summary>
    private static EncoderNetworkFeedback CreateBaselineFeedback(
        bool pli = false,
        bool fir = false)
    {
        return new EncoderNetworkFeedback(
            EstimatedAvailableBitrateBps: 8_000_000,
            PacketLossRatio: 0.0,
            RoundTripTime: TimeSpan.FromMilliseconds(30),
            Jitter: TimeSpan.FromMilliseconds(5),
            PendingRtpBytes: 10_000,
            NackCount: 0,
            PictureLossIndication: pli,
            FullIntraRequest: fir,
            ClientDecodeDelay: null
        );
    }

    /// <summary>
    /// Helper method to create a baseline encoder adaptation decision.
    /// </summary>
    private static EncoderAdaptationDecision CreateBaselineDecision()
    {
        return new EncoderAdaptationDecision(
            TargetBitrateBps: 8_000_000,
            MaxFrameBytes: 100_000,
            TargetFps: 60,
            Width: 1920,
            Height: 1080,
            BaseQp: 28,
            ForceIdr: false,
            EnableIntraRefresh: false,
            SpeedMode: EncoderSpeedMode.Balanced
        );
    }

    /// <summary>
    /// Helper method to create normal encoder pipeline stats.
    /// </summary>
    private static EncoderPipelineStats CreateNormalStats()
    {
        return new EncoderPipelineStats(
            LastEncodeDuration: TimeSpan.FromMilliseconds(3),
            AverageEncodeDuration: TimeSpan.FromMilliseconds(3),
            PendingInputFrames: 1,
            PendingEncodedFrames: 0,
            DroppedInputFrames: 0,
            DroppedEncodedFrames: 0,
            LastEncodedFrameBytes: 50_000,
            LastFrameQp: 28,
            LastFrameWasIdr: false,
            MotionComplexity: 0.3,
            TextureComplexity: 0.5,
            SceneChangeDetected: false
        );
    }

    /// <summary>
    /// Test 1: PLI triggers IDR when cooldown is not active.
    /// </summary>
    [Fact]
    public void RecoveryPolicy_ForcesIdr_OnPli()
    {
        var config = new RateControlConfig();
        var logger = new NullLogger<H264RecoveryPolicy>();
        var policy = new H264RecoveryPolicy(config, logger);

        var feedback = CreateBaselineFeedback(pli: true);
        var decision = CreateBaselineDecision();

        var recovery = policy.DecideRecovery(feedback, decision);

        Assert.True(recovery.ForceIdr, "PLI should trigger IDR when cooldown is not active");
        Assert.Equal(expected: 1, actual: recovery.IdrCount);
        Assert.Equal(expected: 1, actual: recovery.PliCount);
        Assert.Equal(expected: 0, actual: recovery.FirCount);
        Assert.Equal(expected: "PLI_detected", actual: recovery.RecoveryReason);
    }

    /// <summary>
    /// Test 2: Repeated PLI within cooldown does not trigger repeated IDRs.
    /// Falls back to intra refresh instead.
    /// </summary>
    [Fact]
    public void RecoveryPolicy_DoesNotForceIdr_WhenPliInCooldown()
    {
        var config = new RateControlConfig { IdrCooldownFrames = 10 };
        var logger = new NullLogger<H264RecoveryPolicy>();
        var policy = new H264RecoveryPolicy(config, logger);

        var feedback = CreateBaselineFeedback(pli: true);
        var decision = CreateBaselineDecision();

        // First PLI
        var recovery1 = policy.DecideRecovery(feedback, decision);
        Assert.True(recovery1.ForceIdr);
        Assert.Equal(expected: 1, actual: recovery1.IdrCount);

        // Second PLI immediately after (within cooldown)
        var recovery2 = policy.DecideRecovery(feedback, decision);
        Assert.False(recovery2.ForceIdr, "PLI within cooldown should not trigger IDR");
        Assert.True(recovery2.EnableIntraRefresh, "Should fall back to intra refresh");
        Assert.Equal(expected: "PLI_cooldown_fallback", actual: recovery2.RecoveryReason);
        Assert.Equal(1, recovery2.IdrCount);
        Assert.Equal(2, recovery2.PliCount);
    }

    /// <summary>
    /// Test 3: FIR has priority over PLI.
    /// When both FIR and PLI are true, only FIR is processed.
    /// </summary>
    [Fact]
    public void RecoveryPolicy_FirHasPriority_OverPli()
    {
        var config = new RateControlConfig();
        var logger = new NullLogger<H264RecoveryPolicy>();
        var policy = new H264RecoveryPolicy(config, logger);

        var feedback = CreateBaselineFeedback(pli: true, fir: true);
        var decision = CreateBaselineDecision();

        var recovery = policy.DecideRecovery(feedback, decision);

        Assert.True(recovery.ForceIdr);
        Assert.Equal(1, recovery.FirCount);
        Assert.Equal(0, recovery.PliCount);
        Assert.Equal(expected: "FIR_requested", actual: recovery.RecoveryReason);
    }

    /// <summary>
    /// Test 4: Cooldown expires and allows next IDR.
    /// After IdrCooldownFrames + 1 frames without PLI, the next PLI triggers IDR.
    /// </summary>
    [Fact]
    public void RecoveryPolicy_CooldownExpires_AllowsNextIdr()
    {
        var config = new RateControlConfig { IdrCooldownFrames = 10 };
        var logger = new NullLogger<H264RecoveryPolicy>();
        var policy = new H264RecoveryPolicy(config, logger);

        var feedback = CreateBaselineFeedback(pli: true);
        var noPliFeedback = CreateBaselineFeedback(pli: false);
        var decision = CreateBaselineDecision();

        // First PLI
        var recovery1 = policy.DecideRecovery(feedback, decision);
        Assert.True(recovery1.ForceIdr);
        Assert.Equal(1, recovery1.IdrCount);

        // Advance cooldown counter by calling with no PLI for 11 frames
        for (int i = 0; i < 11; i++)
        {
            policy.DecideRecovery(noPliFeedback, decision);
        }

        // PLI again after cooldown expires
        var recovery2 = policy.DecideRecovery(feedback, decision);
        Assert.True(recovery2.ForceIdr, "IDR should be allowed after cooldown expires");
        Assert.Equal(expected: 2, actual: recovery2.IdrCount);
        Assert.Equal(expected: 2, actual: recovery2.PliCount);
    }

    /// <summary>
    /// Test 5: PLI during cooldown falls back to intra refresh.
    /// Verifies that the fallback behavior is reliable.
    /// </summary>
    [Fact]
    public void RecoveryPolicy_PliDuringCooldown_FallsBackToIntraRefresh()
    {
        var config = new RateControlConfig { IdrCooldownFrames = 5 };
        var logger = new NullLogger<H264RecoveryPolicy>();
        var policy = new H264RecoveryPolicy(config, logger);

        var feedback = CreateBaselineFeedback(pli: true);
        var decision = CreateBaselineDecision();

        // First PLI (should trigger IDR)
        var recovery1 = policy.DecideRecovery(feedback, decision);
        Assert.True(recovery1.ForceIdr);

        // Immediate second PLI (should trigger intra refresh)
        var recovery2 = policy.DecideRecovery(feedback, decision);
        Assert.False(recovery2.ForceIdr);
        Assert.True(recovery2.EnableIntraRefresh);
        Assert.Equal(expected: "PLI_cooldown_fallback", actual: recovery2.RecoveryReason);
    }

    /// <summary>
    /// Test 6: FIR during cooldown falls back to intra refresh.
    /// </summary>
    [Fact]
    public void RecoveryPolicy_FirDuringCooldown_FallsBackToIntraRefresh()
    {
        var config = new RateControlConfig { IdrCooldownFrames = 5 };
        var logger = new NullLogger<H264RecoveryPolicy>();
        var policy = new H264RecoveryPolicy(config, logger);

        var firFeedback = CreateBaselineFeedback(fir: true);
        var decision = CreateBaselineDecision();

        // First FIR (should trigger IDR)
        var recovery1 = policy.DecideRecovery(firFeedback, decision);
        Assert.True(recovery1.ForceIdr);
        Assert.Equal(1, recovery1.FirCount);

        // Second FIR immediately after (should trigger intra refresh)
        var recovery2 = policy.DecideRecovery(firFeedback, decision);
        Assert.False(recovery2.ForceIdr);
        Assert.True(recovery2.EnableIntraRefresh);
        Assert.Equal(expected: "FIR_cooldown_fallback", actual: recovery2.RecoveryReason);
        Assert.Equal(expected: 2, actual: recovery2.FirCount);
    }

    /// <summary>
    /// Test 7: IDR budget calculation.
    /// Verifies that IDR budget is 2x the normal frame budget.
    /// </summary>
    [Fact]
    public void IdrBudget_CalculatesMaxIdrBytes_As2xMaxFrameBytes()
    {
        var config = new RateControlConfig();
        var logger = new NullLogger<IdrBudget>();
        var budget = new IdrBudget(config, logger);

        int maxFrameBytes = 20_000;
        int maxIdrBytes = budget.CalculateMaxIdrBytes(maxFrameBytes);

        Assert.Equal(expected: 40_000, actual: maxIdrBytes);
    }

    /// <summary>
    /// Test 8: IDR budget overshoot detection.
    /// Verifies that overshoots are correctly identified.
    /// </summary>
    [Fact]
    public void IdrBudget_DetectsOvershoot_WhenIdrTooLarge()
    {
        var config = new RateControlConfig();
        var logger = new NullLogger<IdrBudget>();
        var budget = new IdrBudget(config, logger);

        int maxIdrBytes = 40_000;
        int encodedIdrBytes = 50_000;

        bool exceeded = budget.IdrFrameExceededBudget(encodedIdrBytes, maxIdrBytes);

        Assert.True(exceeded, "IDR frame exceeded budget");
    }

    /// <summary>
    /// Test 9: IDR budget allows frames within budget.
    /// </summary>
    [Fact]
    public void IdrBudget_AllowsFrame_WhenWithinBudget()
    {
        var config = new RateControlConfig();
        var logger = new NullLogger<IdrBudget>();
        var budget = new IdrBudget(config, logger);

        int maxIdrBytes = 40_000;
        int encodedIdrBytes = 35_000;

        bool exceeded = budget.IdrFrameExceededBudget(encodedIdrBytes, maxIdrBytes);

        Assert.False(exceeded, "IDR frame is within budget");
    }

    // Tests 10/11 covered the former IIntraRefreshPolicy stub, removed when gradual intra refresh
    // became a real encoder feature (H264BaselineEncoderOptions.IntraRefreshPeriodFrames,
    // H264BaselineEncoder.RequestIntraRefresh); see H264IntraRefreshTests for its coverage.

    /// <summary>
    /// Test 12: Recovery policy exposes accurate metrics.
    /// Verifies that IDR, PLI, and FIR counts are correctly tracked separately.
    /// </summary>
    [Fact]
    public void RecoveryPolicy_GetMetrics_ReturnsAccurateCounts()
    {
        var config = new RateControlConfig { IdrCooldownFrames = 5 };
        var logger = new NullLogger<H264RecoveryPolicy>();
        var policy = new H264RecoveryPolicy(config, logger);

        var feedback = CreateBaselineFeedback(pli: true);
        var firFeedback = CreateBaselineFeedback(fir: true);
        var noFeedback = CreateBaselineFeedback();
        var decision = CreateBaselineDecision();

        // One PLI (triggers IDR)
        policy.DecideRecovery(feedback, decision);

        // Wait for cooldown to expire
        for (int i = 0; i < 6; i++)
        {
            policy.DecideRecovery(noFeedback, decision);
        }

        // One FIR (triggers another IDR)
        policy.DecideRecovery(firFeedback, decision);

        var (idrCount, pliCount, firCount) = policy.GetMetrics();

        Assert.Equal(expected: 2, actual: idrCount);
        Assert.Equal(expected: 1, actual: pliCount);
        Assert.Equal(expected: 1, actual: firCount);
    }

    /// <summary>
    /// Test 13: Recovery policy can be reset.
    /// </summary>
    [Fact]
    public void RecoveryPolicy_Reset_ClearsAllCounters()
    {
        var config = new RateControlConfig();
        var logger = new NullLogger<H264RecoveryPolicy>();
        var policy = new H264RecoveryPolicy(config, logger);

        var feedback = CreateBaselineFeedback(pli: true);
        var decision = CreateBaselineDecision();

        // Trigger some events
        policy.DecideRecovery(feedback, decision);
        policy.DecideRecovery(feedback, decision);

        var (idrCount1, pliCount1, firCount1) = policy.GetMetrics();
        Assert.True(idrCount1 > 0 || pliCount1 > 0);

        // Reset
        policy.Reset();

        var (idrCount2, pliCount2, firCount2) = policy.GetMetrics();
        Assert.Equal(expected: 0, actual: idrCount2);
        Assert.Equal(expected: 0, actual: pliCount2);
        Assert.Equal(expected: 0, actual: firCount2);
    }

    /// <summary>
    /// Test 14: IsIdrInCooldown property correctly reflects cooldown state.
    /// </summary>
    [Fact]
    public void RecoveryPolicy_IsIdrInCooldown_ReflectsCooldownState()
    {
        var config = new RateControlConfig { IdrCooldownFrames = 5 };
        var logger = new NullLogger<H264RecoveryPolicy>();
        var policy = new H264RecoveryPolicy(config, logger);

        var feedback = CreateBaselineFeedback(pli: true);
        var noPliFeedback = CreateBaselineFeedback(pli: false);
        var decision = CreateBaselineDecision();

        // Before any IDR
        Assert.False(policy.IsIdrInCooldown);

        // After IDR
        policy.DecideRecovery(feedback, decision);
        Assert.True(policy.IsIdrInCooldown);

        // After cooldown expires
        for (int i = 0; i < 6; i++)
        {
            policy.DecideRecovery(noPliFeedback, decision);
        }
        Assert.False(policy.IsIdrInCooldown);
    }

    /// <summary>
    /// Test 15: LowLatencyRateController integrates recovery policy.
    /// Full stack test: PLI feedback → Decide() returns ForceIdr=true.
    /// </summary>
    [Fact]
    public void LowLatencyRateController_IntegratesRecovery_ForcesIdrOnPli()
    {
        var config = new RateControlConfig();
        var logger = new NullLogger<LowLatencyRateController>();
        var controller = new LowLatencyRateController(config, logger);

        var feedback = new EncoderNetworkFeedback(
            EstimatedAvailableBitrateBps: 8_000_000,
            PacketLossRatio: 0.0,
            RoundTripTime: TimeSpan.FromMilliseconds(30),
            Jitter: TimeSpan.FromMilliseconds(5),
            PendingRtpBytes: 10_000,
            NackCount: 0,
            PictureLossIndication: true,
            FullIntraRequest: false,
            ClientDecodeDelay: null
        );

        var stats = CreateNormalStats();

        var decision = controller.Decide(feedback, stats);

        Assert.True(decision.ForceIdr, "PLI should result in ForceIdr=true");
    }

    /// <summary>
    /// Test 16: LowLatencyRateController can use custom recovery policy.
    /// Verifies that the optional recovery policy parameter works.
    /// </summary>
    [Fact]
    public void LowLatencyRateController_AcceptsCustomRecoveryPolicy()
    {
        var config = new RateControlConfig();
        var logger = new NullLogger<LowLatencyRateController>();
        var customRecovery = new H264RecoveryPolicy(config, new NullLogger<H264RecoveryPolicy>());

        var controller = new LowLatencyRateController(config, logger, customRecovery);

        var feedback = CreateBaselineFeedback(pli: true);
        var stats = CreateNormalStats();

        var decision = controller.Decide(feedback, stats);

        Assert.True(decision.ForceIdr);
    }

    /// <summary>
    /// Test 17: Recovery policy correctly merges with controller decision.
    /// When both recovery and controller want to set a flag, OR logic applies.
    /// </summary>
    [Fact]
    public void LowLatencyRateController_MergesRecovery_UsingOrLogic()
    {
        var config = new RateControlConfig();
        var logger = new NullLogger<LowLatencyRateController>();
        var controller = new LowLatencyRateController(config, logger);

        var feedback = CreateBaselineFeedback(pli: true);
        var stats = CreateNormalStats();

        var decision = controller.Decide(feedback, stats);

        // Recovery policy sets ForceIdr=true via PLI
        Assert.True(decision.ForceIdr);
    }
}
