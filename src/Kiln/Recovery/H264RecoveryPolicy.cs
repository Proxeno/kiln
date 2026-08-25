using Microsoft.Extensions.Logging;
using Kiln.RateControl;

namespace Kiln.Recovery;

/// <summary>
/// Recovery policy for handling picture loss (PLI) and full intra requests (FIR)
/// from the client.
///
/// Implements:
/// - PLI (Picture Loss Indication) handling: triggers IDR or intra refresh
/// - FIR (Full Intra Request) handling: higher priority than PLI
/// - Scene-change IDR: lowest priority; when the encoder reports the previous frame was a scene
///   cut (mostly intra-coded P frame), schedule a clean IDR at the cut, cooldown permitting
/// - IDR cooldown: prevents keyframe storms by bounding IDR frequency
/// - Fallback to intra refresh when cooldown is active (loss recovery only — a scene change in
///   cooldown simply waits for the GOP's next scheduled IDR; nothing was lost)
/// </summary>
public sealed class H264RecoveryPolicy
{
    private readonly RateControlConfig _config;
    private readonly ILogger<H264RecoveryPolicy> _logger;
    private int _idrCooldownCounter = 0;
    private int _idrCount = 0;
    private int _pliCount = 0;
    private int _firCount = 0;

    /// <summary>
    /// Constructs a recovery policy with the given configuration and logger.
    /// Initializes all counters to zero.
    /// </summary>
    /// <param name="config">Rate control configuration containing IDR cooldown settings.</param>
    /// <param name="logger">Logger for observability and debugging.</param>
    public H264RecoveryPolicy(RateControlConfig config, ILogger<H264RecoveryPolicy> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Decide on recovery action based on client feedback (PLI/FIR) and encoder pipeline stats.
    ///
    /// FIR has priority over PLI. If both are true, only FIR is processed.
    /// If cooldown is active, recovery signals trigger intra refresh fallback
    /// instead of IDR emission.
    ///
    /// Scene change (<see cref="EncoderPipelineStats.SceneChangeDetected"/>) has the lowest
    /// priority: with no loss signal pending and the cooldown clear, it forces an IDR so the frame
    /// after a cut is a clean random-access point instead of predicting from the mostly-intra cut
    /// frame. In cooldown it does nothing — nothing was lost, and the encoder reports a cut only on
    /// the single P frame that coded it, so the request does not repeat.
    ///
    /// Cooldown counter is decremented each call (but not below 0).
    /// </summary>
    /// <param name="feedback">Current network feedback including PLI/FIR signals.</param>
    /// <param name="currentDecision">Current encoder adaptation decision (used for context).</param>
    /// <param name="stats">Encoder pipeline stats for the scene-change signal; null skips it.</param>
    /// <returns>Recovery decision specifying IDR, intra refresh, and metrics.</returns>
    public RecoveryDecision DecideRecovery(
        EncoderNetworkFeedback feedback,
        EncoderAdaptationDecision currentDecision,
        EncoderPipelineStats? stats = null)
    {
        if (feedback == null)
        {
            throw new ArgumentNullException(nameof(feedback));
        }

        if (currentDecision == null)
        {
            throw new ArgumentNullException(nameof(currentDecision));
        }

        var forceIdr = false;
        var enableIntraRefresh = false;
        var recoveryReason = "";

        // Handle FIR with highest priority
        if (feedback.FullIntraRequest)
        {
            _firCount++;
            if (_idrCooldownCounter <= 0)
            {
                forceIdr = true;
                _idrCount++;
                _idrCooldownCounter = _config.IdrCooldownFrames;
                recoveryReason = "FIR_requested";
                _logger.LogInformation("FIR received. Forcing IDR. FIR count: {count}", _firCount);
            }
            else
            {
                // FIR is in cooldown; use intra refresh instead
                enableIntraRefresh = true;
                recoveryReason = "FIR_cooldown_fallback";
                _logger.LogInformation("FIR received but in IDR cooldown. Using intra refresh.");
            }
        }
        // Handle PLI with lower priority (only if FIR was not true)
        else if (feedback.PictureLossIndication)
        {
            _pliCount++;
            if (_idrCooldownCounter <= 0)
            {
                forceIdr = true;
                _idrCount++;
                _idrCooldownCounter = _config.IdrCooldownFrames;
                recoveryReason = "PLI_detected";
                _logger.LogInformation("PLI received. Forcing IDR. PLI count: {count}", _pliCount);
            }
            else
            {
                // PLI is in cooldown; use intra refresh instead
                enableIntraRefresh = true;
                recoveryReason = "PLI_cooldown_fallback";
                _logger.LogInformation("PLI received but in IDR cooldown. Using intra refresh.");
            }
        }
        // Scene change reported by the encoder: lowest priority, IDR only, no cooldown fallback
        else if (stats is { SceneChangeDetected: true } && _idrCooldownCounter <= 0)
        {
            forceIdr = true;
            _idrCount++;
            _idrCooldownCounter = _config.IdrCooldownFrames;
            recoveryReason = "scene_change";
            _logger.LogInformation("Scene change reported by encoder. Forcing IDR.");
        }

        // Decrement cooldown counter each frame (but not below 0)
        if (_idrCooldownCounter > 0)
        {
            _idrCooldownCounter--;
        }

        return new RecoveryDecision(
            ForceIdr: forceIdr,
            EnableIntraRefresh: enableIntraRefresh,
            RecoveryReason: recoveryReason,
            IdrCount: _idrCount,
            PliCount: _pliCount,
            FirCount: _firCount
        );
    }

    /// <summary>
    /// Check if IDR is currently in cooldown period.
    /// </summary>
    public bool IsIdrInCooldown => _idrCooldownCounter > 0;

    /// <summary>
    /// Get recovery metrics for monitoring and observability.
    /// </summary>
    /// <returns>A tuple of (IDR count, PLI count, FIR count).</returns>
    public (int IdrCount, int PliCount, int FirCount) GetMetrics()
    {
        return (_idrCount, _pliCount, _firCount);
    }

    /// <summary>
    /// Reset recovery counters and cooldown state.
    /// Used on scene change, stream reset, or configuration change.
    /// </summary>
    public void Reset()
    {
        _idrCooldownCounter = 0;
        _idrCount = 0;
        _pliCount = 0;
        _firCount = 0;
    }
}
