using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Kiln.Recovery;

namespace Kiln.RateControl;

/// <summary>
/// Adaptive rate controller for low-latency H.264 cloud gaming.
/// Makes per-frame decisions about bitrate, resolution, frame rate, and quality
/// in response to network and encoder state feedback.
/// <para>
/// OWNERSHIP CONTRACT — this controller integrates the recovery policy itself: each
/// <see cref="Decide"/> call invokes <see cref="H264RecoveryPolicy.DecideRecovery"/> exactly once and
/// folds <c>ForceIdr</c> / <c>EnableIntraRefresh</c> into the returned decision. A composing layer must
/// therefore NOT call the recovery policy again, and must pass <em>this controller's</em> recovery
/// instance (not a second one) anywhere else it is needed — otherwise the stateful IDR cooldown is
/// advanced twice per frame, halving keyframe-storm protection and double-counting metrics. (This is
/// the exact trap a former master controller fell into.)
/// </para>
/// </summary>
public sealed class LowLatencyRateController
{
    private readonly RateControlConfig _config;
    private readonly ILogger<LowLatencyRateController> _logger;
    private readonly H264RecoveryPolicy _recoveryPolicy;
    private RateControlState _state;

    /// <summary>
    /// Constructs a rate controller with the given configuration and logger.
    /// Initializes state to sensible defaults from the configuration.
    /// </summary>
    /// <param name="config">Rate control configuration with tuning parameters.</param>
    /// <param name="logger">Logger for observability and debugging.</param>
    /// <param name="recoveryPolicy">Optional recovery policy instance. If null, creates one with NullLogger.</param>
    public LowLatencyRateController(
        RateControlConfig config,
        ILogger<LowLatencyRateController> logger,
        H264RecoveryPolicy? recoveryPolicy = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _recoveryPolicy = recoveryPolicy ?? new H264RecoveryPolicy(config, NullLogger<H264RecoveryPolicy>.Instance);

        _state = new RateControlState
        {
            TargetBitrateBps = config.InitialTargetBitrateBps,
            BaseQp = config.BaseQp,
            TargetFps = 60,
            Width = 1920,
            Height = 1080,
            SpeedMode = EncoderSpeedMode.Balanced
        };
    }

    /// <summary>
    /// The recovery policy this controller drives (see the ownership contract on the class). A
    /// composing layer that needs to reset it, read its metrics, or otherwise reference recovery must
    /// use THIS instance rather than constructing its own, and must never invoke
    /// <see cref="H264RecoveryPolicy.DecideRecovery"/> on it (the controller already does, once per
    /// <see cref="Decide"/>).
    /// </summary>
    public H264RecoveryPolicy RecoveryPolicy => _recoveryPolicy;

    /// <summary>
    /// Decides on the next encoder configuration based on network and pipeline feedback.
    /// Implements Phase 2 rate control logic: bitrate adaptation, QP adjustment,
    /// and encode backpressure handling.
    /// </summary>
    /// <param name="feedback">Current network state and transport metrics.</param>
    /// <param name="stats">Current encoder pipeline and frame metrics.</param>
    /// <returns>Adaptation decision specifying target bitrate, resolution, quality, and mode.</returns>
    public EncoderAdaptationDecision Decide(
        EncoderNetworkFeedback feedback,
        EncoderPipelineStats stats)
    {
        if (feedback == null)
        {
            throw new ArgumentNullException(nameof(feedback));
        }

        if (stats == null)
        {
            throw new ArgumentNullException(nameof(stats));
        }

        _logger.LogTrace(
            "Frame decision. feedback={feedback}, stats={stats}",
            feedback,
            stats);

        // 1. Detect congestion and encode backpressure
        var congestion = IsCongestioned(feedback, stats);
        var encodeBackpressure = stats.AverageEncodeDuration.TotalMilliseconds >
            (1000.0 / _state.TargetFps) * 0.75;

        // 2. Adapt bitrate
        if (congestion)
        {
            _state.TargetBitrateBps = Math.Max(
                _config.MinTargetBitrateBps,
                (int)(_state.TargetBitrateBps * _config.DownshiftFactor)
            );
            _state.StableFrameCounter = 0;

            _logger.LogInformation(
                "Congestion detected. Downshifting bitrate: {downshiftedBitrate} bps. " +
                "Reason: loss={loss}, rtt={rtt}ms, queue={queue}bytes, nacks={nacks}",
                _state.TargetBitrateBps,
                feedback.PacketLossRatio,
                feedback.RoundTripTime.TotalMilliseconds,
                feedback.PendingRtpBytes,
                feedback.NackCount
            );
        }
        else if (_state.StableFrameCounter >= _config.StabilityWindowFrames)
        {
            _state.TargetBitrateBps = Math.Min(
                _config.MaxTargetBitrateBps,
                (int)(_state.TargetBitrateBps * _config.UpshiftFactor)
            );

            _logger.LogInformation(
                "Network stable for {frames} frames. Upshifting bitrate: {upshiftedBitrate} bps",
                _config.StabilityWindowFrames,
                _state.TargetBitrateBps
            );

            _state.StableFrameCounter = 0;
        }
        else
        {
            _state.StableFrameCounter++;
        }

        // 3. Adjust QP based on bitrate relative to initial
        int adaptedQp = _config.BaseQp;

        if (_state.TargetBitrateBps < _config.InitialTargetBitrateBps * 0.5)
        {
            adaptedQp += 3;
        }
        else if (_state.TargetBitrateBps < _config.InitialTargetBitrateBps * 0.7)
        {
            adaptedQp += 1;
        }

        // If network is very bad, additional increase
        if (feedback.PacketLossRatio > 0.05)
        {
            adaptedQp += 2;
        }

        // Clamp to valid range
        _state.BaseQp = Math.Clamp(adaptedQp, _config.MinQp, _config.MaxQp);

        // 4. Handle encode backpressure
        if (encodeBackpressure)
        {
            _state.SpeedMode = (EncoderSpeedMode)Math.Min(
                (int)EncoderSpeedMode.VeryFast,
                (int)_state.SpeedMode + 1
            );
            _state.BaseQp = Math.Min(_config.MaxQp, _state.BaseQp + 2);

            _logger.LogInformation(
                "Encode backpressure detected. Switching to speed mode {mode}, increasing QP by 2",
                _state.SpeedMode
            );
        }

        // 5. Calculate max frame bytes
        var maxFrameBytes = CalculateMaxFrameBytes();

        // 6. Detect overshoots
        if (stats.LastEncodedFrameBytes > maxFrameBytes)
        {
            _state.FrameSizeOvershoots++;

            _logger.LogWarning(
                "Frame size overshoot: {actual} > {max} bytes. Overshoots: {count}",
                stats.LastEncodedFrameBytes,
                maxFrameBytes,
                _state.FrameSizeOvershoots
            );
        }

        // 7. Return decision
        var decision = new EncoderAdaptationDecision(
            TargetBitrateBps: _state.TargetBitrateBps,
            MaxFrameBytes: maxFrameBytes,
            TargetFps: _state.TargetFps,
            Width: _state.Width,
            Height: _state.Height,
            BaseQp: _state.BaseQp,
            ForceIdr: false,
            EnableIntraRefresh: false,
            SpeedMode: _state.SpeedMode
        );

        // 8. Integrate recovery policy (Phase 4)
        var recovery = _recoveryPolicy.DecideRecovery(feedback, decision);
        decision = decision with
        {
            ForceIdr = recovery.ForceIdr || decision.ForceIdr,
            EnableIntraRefresh = recovery.EnableIntraRefresh || decision.EnableIntraRefresh
        };

        _logger.LogTrace("Decision: {decision}", decision);
        return decision;
    }

    /// <summary>
    /// Detects whether network conditions indicate congestion based on multiple signals.
    /// </summary>
    /// <param name="feedback">Current network feedback.</param>
    /// <param name="stats">Current pipeline stats.</param>
    /// <returns>True if congestion is detected, false otherwise.</returns>
    private bool IsCongestioned(
        EncoderNetworkFeedback feedback,
        EncoderPipelineStats stats)
    {
        var hasPacketLoss = feedback.PacketLossRatio > _config.CongestionPacketLossThreshold;
        var hasRttSpike = feedback.RoundTripTime.TotalMilliseconds > 50;
        var hasSendQueueBacklog = feedback.PendingRtpBytes > _config.MaxPendingRtpBytes;
        var hasNackBurst = feedback.NackCount > 5;

        return hasPacketLoss || hasRttSpike || hasSendQueueBacklog || hasNackBurst;
    }

    /// <summary>
    /// Calculates the maximum byte size a single frame should not exceed.
    /// Formula: (targetBitrate / 8 bits/byte / targetFps frames/sec) * burstAllowance.
    /// </summary>
    /// <returns>Maximum frame size in bytes.</returns>
    private int CalculateMaxFrameBytes()
    {
        var averageFrameBytes = _state.TargetBitrateBps / 8.0 / _state.TargetFps;
        var maxFrameBytes = (int)(averageFrameBytes * _config.BurstAllowance);
        return Math.Max(100, maxFrameBytes);
    }
}
