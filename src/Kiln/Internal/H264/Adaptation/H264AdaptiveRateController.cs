using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Kiln.RateControl;
using Kiln.Recovery;

namespace Kiln.Internal.H264.Adaptation;

/// <summary>
/// Master control loop integrating all five phases of the adaptive rate-control system. This is the
/// single entry point for the encoder/pipeline to turn network + pipeline feedback into a complete
/// per-frame <see cref="EncoderAdaptationDecision"/>:
/// <list type="bullet">
/// <item>Phase 2 — bitrate, QP, max frame bytes (<see cref="LowLatencyRateController"/>).</item>
/// <item>Phase 4 — IDR / intra-refresh recovery, applied <b>once</b> inside Phase 2 (see below).</item>
/// <item>Phase 5 — resolution, fps, and speed-mode adaptation (<see cref="AdaptationPolicy"/>).</item>
/// </list>
/// Recovery is NOT applied here: <see cref="LowLatencyRateController.Decide"/> already folds it into
/// its result using a single recovery instance. Re-applying it (as an earlier version did, with a
/// second policy instance) double-advanced the stateful IDR cooldown — defeating keyframe-storm
/// protection — and double-counted metrics. This controller therefore reuses the rate controller's
/// recovery instance (<see cref="LowLatencyRateController.RecoveryPolicy"/>) for reset/metrics only.
/// </summary>
public sealed class H264AdaptiveRateController
{
    private readonly LowLatencyRateController _rateController;
    private readonly AdaptationPolicy _adaptationPolicy;
    private readonly ILogger<H264AdaptiveRateController> _logger;

    public H264AdaptiveRateController(
        RateControlConfig config,
        ILogger<H264AdaptiveRateController>? logger = null,
        ILogger<LowLatencyRateController>? rateControllerLogger = null,
        ILogger<H264RecoveryPolicy>? recoveryLogger = null,
        ILogger<AdaptationPolicy>? adaptationLogger = null,
        H264RecoveryPolicy? recoveryPolicy = null,
        ResolutionLadder? resolutionLadder = null,
        FpsLadder? fpsLadder = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _logger = logger ?? NullLogger<H264AdaptiveRateController>.Instance;

        // One recovery instance, owned by the rate controller. Pass it in so callers that want to
        // observe/reset recovery share the exact instance Decide drives.
        var recovery = recoveryPolicy ?? new H264RecoveryPolicy(config, recoveryLogger ?? NullLogger<H264RecoveryPolicy>.Instance);
        _rateController = new LowLatencyRateController(
            config, rateControllerLogger ?? NullLogger<LowLatencyRateController>.Instance, recovery);
        _adaptationPolicy = new AdaptationPolicy(config, resolutionLadder, fpsLadder, adaptationLogger);
    }

    /// <summary>The recovery policy driving IDR/intra-refresh decisions (already invoked once per
    /// <see cref="GetDecision"/> by the rate controller — do not call it again).</summary>
    public H264RecoveryPolicy RecoveryPolicy => _rateController.RecoveryPolicy;

    /// <summary>Single entry point: produce the complete adaptation decision for the next frame.</summary>
    public EncoderAdaptationDecision GetDecision(
        EncoderNetworkFeedback feedback,
        EncoderPipelineStats stats)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        ArgumentNullException.ThrowIfNull(stats);

        // Phase 2 + Phase 4: rate control with recovery already integrated.
        var rateDecision = _rateController.Decide(feedback, stats);

        // Phase 5: resolution / fps / speed-mode adaptation layered on top.
        var adaptation = _adaptationPolicy.DecideAdaptation(feedback, stats, rateDecision);
        var finalDecision = rateDecision with
        {
            Width = adaptation.Width,
            Height = adaptation.Height,
            TargetFps = adaptation.Fps,
            SpeedMode = adaptation.SpeedMode,
        };

        _logger.LogTrace(
            "Decision: bitrate={bitrate}bps, {width}x{height}@{fps}, qp={qp}, forceIdr={idr}, speed={speed}",
            finalDecision.TargetBitrateBps, finalDecision.Width, finalDecision.Height,
            finalDecision.TargetFps, finalDecision.BaseQp, finalDecision.ForceIdr, finalDecision.SpeedMode);

        return finalDecision;
    }

    /// <summary>Reset adaptation + recovery state (e.g. on stream reset or scene change).</summary>
    public void Reset()
    {
        _adaptationPolicy.Reset();
        RecoveryPolicy.Reset();
    }

    /// <summary>Last resolution/fps/speed adaptation reason, for observability.</summary>
    public string LastAdaptationReason => _adaptationPolicy.LastAdaptationReason;
}
