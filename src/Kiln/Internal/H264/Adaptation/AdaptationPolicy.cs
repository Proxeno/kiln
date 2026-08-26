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

    /// <summary>
    /// Tracked baseline RTT in milliseconds for the severe-tier and recovery multiplier tests
    /// (<see cref="RateControlConfig.SevereCongestionRttMultiplier"/> /
    /// <see cref="RateControlConfig.RecoveryRttMultiplier"/>). Same
    /// <see cref="NetworkSignalBaseline"/> math as
    /// <see cref="RateControlState.BaselineRttMs"/>, updated once per
    /// <see cref="DecideAdaptation"/> from caller-supplied feedback only, so an identical feedback
    /// sequence yields the identical baseline in both components. Deliberately not cleared by
    /// <see cref="Reset"/>: a stream reset does not change the network path.
    /// </summary>
    private double _baselineRttMs;

    /// <summary>
    /// Tracked baseline jitter in milliseconds, mirroring
    /// <see cref="RateControlState.BaselineJitterMs"/> the same way <see cref="_baselineRttMs"/>
    /// mirrors the RTT baseline. A jitter spike (queueing early warning) only tempers the
    /// walk-up — it never joins the severe tier, because rising jitter without loss is transient
    /// queueing, the case where cascading down is exactly wrong.
    /// </summary>
    private double _baselineJitterMs;

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

        // Fold this frame's RTT and jitter into the baselines before anything can early-return, so
        // a route change keeps drifting in during cooldown frames too.
        NetworkSignalBaseline.Update(ref _baselineRttMs, feedback.RoundTripTime.TotalMilliseconds);
        NetworkSignalBaseline.Update(ref _baselineJitterMs, feedback.Jitter.TotalMilliseconds);

        if (_adaptationCooldownCounter > 0)
        {
            _adaptationCooldownCounter--;
            return new AdaptationDecision(newWidth, newHeight, newFps, newSpeedMode, "");
        }

        var severe =
            feedback.PacketLossRatio > 0.10 ||
            IsSevereRttSpike(feedback.RoundTripTime) ||
            feedback.PendingRtpBytes > 200_000 ||
            stats.AverageEncodeDuration.TotalMilliseconds > (1000.0 / currentDecision.TargetFps) * 0.90;

        // A client that cannot decode in real time is a different problem from network
        // congestion, but the right response is the same complexity cascade: cutting bitrate does
        // not help a decoder that cannot keep up, whereas a faster speed mode (simpler
        // bitstreams), fewer frames per second, or a lower resolution all reduce its per-second
        // decode work. The bitrate path never sees this signal.
        var clientBehind = IsClientBehind(feedback, currentDecision.TargetFps, _config.ClientDecodeDelayBudgetFactor);

        if (severe || clientBehind)
        {
            // Reason strings carry the trigger; severe congestion wins the label when both fire
            // (it is the stronger claim — the network is failing, not just the client).
            var cause = severe ? "severe_congestion" : "client_decode_delay";

            // Cascade down: speed mode first (cheapest), then fps, then resolution (most visible).
            if (currentDecision.SpeedMode < EncoderSpeedMode.VeryFast)
            {
                newSpeedMode = (EncoderSpeedMode)((int)currentDecision.SpeedMode + 1);
                reason = "speed_mode_increase_" + cause;
            }
            else if (_fpsLadder.GetLowerFps(currentDecision.TargetFps) is int lowerFps)
            {
                newFps = lowerFps;
                reason = "fps_reduction_" + cause;
            }
            else if (_resolutionLadder.GetLowerResolution(
                         new ResolutionLadder.Resolution(currentDecision.Width, currentDecision.Height, "current"))
                     is { } lowerRes)
            {
                newWidth = lowerRes.Width;
                newHeight = lowerRes.Height;
                reason = "resolution_reduction_" + cause;
            }

            if (reason.Length > 0)
            {
                _logger.LogInformation(
                    "Cascading down → {reason}. loss={loss}, rtt={rtt}ms, queue={queue}, encode={encode}ms, decodeDelay={decode}ms",
                    reason, feedback.PacketLossRatio, feedback.RoundTripTime.TotalMilliseconds,
                    feedback.PendingRtpBytes, stats.AverageEncodeDuration.TotalMilliseconds,
                    feedback.ClientDecodeDelay?.TotalMilliseconds ?? 0);
                _adaptationCooldownCounter = _config.AdaptationCooldownFrames;
            }
        }
        else if (IsStableAndCanRecover(feedback, stats, currentDecision.TargetFps))
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

    /// <summary>
    /// Severe-tier RTT test against the tracked baseline: severe when RTT exceeds both
    /// (baseline * <see cref="RateControlConfig.SevereCongestionRttMultiplier"/>) and
    /// <see cref="RateControlConfig.SevereCongestionRttFloor"/> — the same treatment the ordinary
    /// congestion test received when its fixed 50 ms threshold was replaced. The historical fixed
    /// 100 ms test classified every link with a baseline RTT above it as permanently severe and
    /// cascaded it to the bottom of every ladder. Until the first positive RTT sample there is no
    /// baseline and the test never fires (the other severe signals still apply).
    /// </summary>
    private bool IsSevereRttSpike(TimeSpan roundTripTime)
    {
        var rttMs = roundTripTime.TotalMilliseconds;
        return _baselineRttMs > 0
            && rttMs > _baselineRttMs * _config.SevereCongestionRttMultiplier
            && rttMs > _config.SevereCongestionRttFloor.TotalMilliseconds;
    }

    /// <summary>
    /// Recovery gate for the walk-up. The RTT term accepts either the historical absolute 40 ms
    /// gate or RTT within (baseline * <see cref="RateControlConfig.RecoveryRttMultiplier"/>) —
    /// without the baseline-relative term, a link whose propagation RTT exceeds 40 ms could
    /// cascade down on a loss episode and then never walk back up. A jitter spike (jitter above
    /// both baseline * <see cref="RateControlConfig.JitterSpikeMultiplier"/> and
    /// <see cref="RateControlConfig.JitterSpikeFloor"/>) defers the walk-up too: queues are
    /// already building, so raising resolution/fps into them would feed the very congestion the
    /// early warning predicts. Zero jitter (callers without a measurement) never defers.
    /// </summary>
    private bool IsStableAndCanRecover(EncoderNetworkFeedback feedback, EncoderPipelineStats stats, int targetFps)
    {
        var rttMs = feedback.RoundTripTime.TotalMilliseconds;
        var rttSettled = rttMs < 40 ||
            (_baselineRttMs > 0 && rttMs < _baselineRttMs * _config.RecoveryRttMultiplier);
        var jitterMs = feedback.Jitter.TotalMilliseconds;
        var jitterSpike = jitterMs > 0 &&
            _baselineJitterMs > 0 &&
            jitterMs > _baselineJitterMs * _config.JitterSpikeMultiplier &&
            jitterMs > _config.JitterSpikeFloor.TotalMilliseconds;
        return feedback.PacketLossRatio < 0.01 &&
            rttSettled &&
            !jitterSpike &&
            // Walk-up needs the client comfortably inside its decode budget (half of what
            // triggers the cascade — the same stricter-than-trigger pattern as the loss/RTT/queue
            // terms above), so a marginal decoder is not fed more work the moment it catches up.
            !IsClientBehind(feedback, targetFps, _config.ClientDecodeDelayBudgetFactor * 0.5) &&
            feedback.PendingRtpBytes < 20_000 &&
            stats.AverageEncodeDuration.TotalMilliseconds < (1000.0 / 60) * 0.5;
    }

    /// <summary>
    /// Is the client's reported decode delay beyond <paramref name="budgetFactor"/> of the frame
    /// interval at <paramref name="targetFps"/>? Null or non-positive delay (callers without a
    /// measurement, the record default) is never behind. The test is relative to the current frame
    /// interval, so an fps reduction that gives the decoder more time per frame clears it
    /// naturally — built-in hysteresis against ping-ponging the cascade.
    /// </summary>
    private static bool IsClientBehind(EncoderNetworkFeedback feedback, int targetFps, double budgetFactor)
    {
        if (feedback.ClientDecodeDelay is not { } decodeDelay || decodeDelay <= TimeSpan.Zero)
        {
            return false;
        }

        var frameIntervalMs = 1000.0 / Math.Max(1, targetFps);
        return decodeDelay.TotalMilliseconds > frameIntervalMs * budgetFactor;
    }

    public string LastAdaptationReason => _lastAdaptationReason;

    public void Reset()
    {
        _adaptationCooldownCounter = 0;
        _lastAdaptationReason = "";
    }
}
