using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Kiln.RateControl;

namespace Kiln.Internal.H264.Adaptation;

/// <summary>
/// Decides dynamic resolution, frame-rate, and encoder speed-mode changes. Under severe congestion it
/// cascades down (speed mode → fps → resolution); under sustained stability it walks back up in the
/// reverse order. A cooldown prevents flapping (resolution/fps ping-pong).
/// </summary>
public sealed class AdaptationPolicy
{
    private readonly RateControlConfig _config;
    private readonly ResolutionLadder _resolutionLadder;
    private readonly FpsLadder _fpsLadder;
    private readonly ILogger<AdaptationPolicy> _logger;

    private int _adaptationCooldownCounter;
    private string _lastAdaptationReason = "";

    public AdaptationPolicy(
        RateControlConfig config,
        ResolutionLadder? resolutionLadder = null,
        FpsLadder? fpsLadder = null,
        ILogger<AdaptationPolicy>? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _resolutionLadder = resolutionLadder ?? new ResolutionLadder();
        _fpsLadder = fpsLadder ?? new FpsLadder();
        _logger = logger ?? NullLogger<AdaptationPolicy>.Instance;
    }

    /// <summary>Decide resolution / fps / speed-mode changes for the next frame.</summary>
    public AdaptationDecision DecideAdaptation(
        EncoderNetworkFeedback feedback,
        EncoderPipelineStats stats,
        EncoderAdaptationDecision currentDecision)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(currentDecision);

        var newWidth = currentDecision.Width;
        var newHeight = currentDecision.Height;
        var newFps = currentDecision.TargetFps;
        var newSpeedMode = currentDecision.SpeedMode;
        var reason = "";

        if (_adaptationCooldownCounter > 0)
        {
            _adaptationCooldownCounter--;
            return new AdaptationDecision(newWidth, newHeight, newFps, newSpeedMode, "");
        }

        var severe =
            feedback.PacketLossRatio > 0.10 ||
            feedback.RoundTripTime.TotalMilliseconds > 100 ||
            feedback.PendingRtpBytes > 200_000 ||
            stats.AverageEncodeDuration.TotalMilliseconds > (1000.0 / currentDecision.TargetFps) * 0.90;

        if (severe)
        {
            // Cascade down: speed mode first (cheapest), then fps, then resolution (most visible).
            if (currentDecision.SpeedMode < EncoderSpeedMode.VeryFast)
            {
                newSpeedMode = (EncoderSpeedMode)((int)currentDecision.SpeedMode + 1);
                reason = "speed_mode_increase_severe_congestion";
            }
            else if (_fpsLadder.GetLowerFps(currentDecision.TargetFps) is int lowerFps)
            {
                newFps = lowerFps;
                reason = "fps_reduction_severe_congestion";
            }
            else if (_resolutionLadder.GetLowerResolution(
                         new ResolutionLadder.Resolution(currentDecision.Width, currentDecision.Height, "current"))
                     is { } lowerRes)
            {
                newWidth = lowerRes.Width;
                newHeight = lowerRes.Height;
                reason = "resolution_reduction_severe_congestion";
            }

            if (reason.Length > 0)
            {
                _logger.LogInformation(
                    "Severe congestion → {reason}. loss={loss}, rtt={rtt}ms, queue={queue}, encode={encode}ms",
                    reason, feedback.PacketLossRatio, feedback.RoundTripTime.TotalMilliseconds,
                    feedback.PendingRtpBytes, stats.AverageEncodeDuration.TotalMilliseconds);
                _adaptationCooldownCounter = _config.AdaptationCooldownFrames;
            }
        }
        else if (IsStableAndCanRecover(feedback, stats))
        {
            // Walk back up: resolution first (biggest quality win), then fps, then speed mode.
            if (_resolutionLadder.GetHigherResolution(
                    new ResolutionLadder.Resolution(currentDecision.Width, currentDecision.Height, "current"))
                is { } higherRes)
            {
                newWidth = higherRes.Width;
                newHeight = higherRes.Height;
                reason = "resolution_increase_stable";
            }
            else if (_fpsLadder.GetHigherFps(currentDecision.TargetFps) is int higherFps)
            {
                newFps = higherFps;
                reason = "fps_increase_stable";
            }
            else if (newSpeedMode > EncoderSpeedMode.HighQuality)
            {
                newSpeedMode = (EncoderSpeedMode)((int)newSpeedMode - 1);
                reason = "speed_mode_decrease_stable";
            }

            if (reason.Length > 0)
            {
                _logger.LogInformation("Network stable → {reason}", reason);
                _adaptationCooldownCounter = _config.AdaptationCooldownFrames;
            }
        }

        if (reason.Length > 0)
            _lastAdaptationReason = reason;

        return new AdaptationDecision(newWidth, newHeight, newFps, newSpeedMode, reason);
    }

    private static bool IsStableAndCanRecover(EncoderNetworkFeedback feedback, EncoderPipelineStats stats) =>
        feedback.PacketLossRatio < 0.01 &&
        feedback.RoundTripTime.TotalMilliseconds < 40 &&
        feedback.PendingRtpBytes < 20_000 &&
        stats.AverageEncodeDuration.TotalMilliseconds < (1000.0 / 60) * 0.5;

    public string LastAdaptationReason => _lastAdaptationReason;

    public void Reset()
    {
        _adaptationCooldownCounter = 0;
        _lastAdaptationReason = "";
    }
}
