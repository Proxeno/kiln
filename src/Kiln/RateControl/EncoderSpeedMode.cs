namespace Kiln.RateControl;

/// <summary>
/// Encoder speed/quality trade-off mode: a preset ladder over the encoder's measured
/// motion-search knobs (<c>MaxReferenceFrames</c>, <c>UseMotionSatd</c>,
/// <c>SubPartitionRangeCap</c>, <c>MotionSearchEffortCapPerMb</c>), applied via
/// <c>H264BaselineEncoderOptions.SpeedMode</c>. Explicitly assigned knobs always win over the
/// mode; every rung keeps bitstreams deterministic (work budgets are counted algorithmic work,
/// never wall clock). <see cref="LowLatencyRateController"/> also emits a recommended mode in
/// <see cref="EncoderAdaptationDecision"/> for callers driving the encoder from network feedback.
/// See the README performance section for each rung's measured latency/quality position.
/// </summary>
public enum EncoderSpeedMode
{
    /// <summary>
    /// Highest compression, slowest encoding — the encoder-options default, byte-identical to the
    /// historical default behaviour (2 reference frames, SATD motion scoring, full sub-partition
    /// range, no effort cap). Use when bitrate is scarce and latency is flexible.
    /// </summary>
    HighQuality = 0,

    /// <summary>
    /// Near-free speed wins: single reference frame plus a worst-case motion-search effort ceiling
    /// that typical content never touches. Quality within measurement noise of
    /// <see cref="HighQuality"/> on typical content; recommended for most real-time scenarios.
    /// </summary>
    Balanced = 1,

    /// <summary>
    /// Faster encoding at a small compression cost: <see cref="Balanced"/> plus a reduced
    /// sub-partition search radius and a tighter effort ceiling. Use under moderate load or when
    /// latency is critical.
    /// </summary>
    Fast = 2,

    /// <summary>
    /// Fastest encoding with a visible compression cost (SAD-scored integer motion search and a
    /// hard effort ceiling). Use only under severe load when latency is paramount.
    /// </summary>
    VeryFast = 3
}
