using Microsoft.Extensions.Logging;

namespace Kiln.RateControl;

/// <summary>
/// Configuration parameters for the adaptive rate controller.
/// All parameters are tunable and have sensible defaults for cloud-gaming scenarios.
/// </summary>
public sealed class RateControlConfig
{
    /// <summary>Minimum allowed target bitrate (bps). Default: 500 kbps.</summary>
    public int MinTargetBitrateBps { get; init; } = 500_000;

    /// <summary>Maximum allowed target bitrate (bps). Default: 50 Mbps.</summary>
    public int MaxTargetBitrateBps { get; init; } = 50_000_000;

    /// <summary>Initial target bitrate before any adaptation (bps). Default: 8 Mbps.</summary>
    public int InitialTargetBitrateBps { get; init; } = 8_000_000;

    /// <summary>Minimum quality parameter (higher quality). Default: 10.</summary>
    public int MinQp { get; init; } = 10;

    /// <summary>Maximum quality parameter (lower quality). Default: 51.</summary>
    public int MaxQp { get; init; } = 51;

    /// <summary>Default starting quality parameter. Default: 28.</summary>
    public int BaseQp { get; init; } = 28;

    /// <summary>
    /// Burst allowance multiplier for MaxFrameBytes calculation.
    /// MaxFrameBytes = (TargetBitrateBps / 8.0 / TargetFps) * BurstAllowance.
    /// Default: 2.0 (frames can be up to 2x average size).
    /// </summary>
    public double BurstAllowance { get; init; } = 2.0;

    /// <summary>
    /// Multiplicative factor for downward bitrate adaptation during congestion.
    /// Default: 0.7 (reduce to 70% of current rate).
    /// </summary>
    public double DownshiftFactor { get; init; } = 0.7;

    /// <summary>
    /// Multiplicative factor for upward bitrate adaptation during stability.
    /// Default: 1.05 (increase by 5% per frame).
    /// </summary>
    public double UpshiftFactor { get; init; } = 1.05;

    /// <summary>
    /// Packet loss ratio threshold above which congestion is declared (0.0-1.0).
    /// Default: 0.02 (2% loss).
    /// </summary>
    public double CongestionPacketLossThreshold { get; init; } = 0.02;

    /// <summary>
    /// Packet loss ratio threshold below which recovery (upshift) is allowed.
    /// Default: 0.005 (0.5% loss).
    /// </summary>
    public double RecoveryPacketLossThreshold { get; init; } = 0.005;

    /// <summary>
    /// RTT multiplier threshold for congestion detection.
    /// The controller tracks a baseline RTT from the feedback it is fed (fastest sample seen, with
    /// a slow per-decision upward drift so a route change eventually becomes the new baseline);
    /// congestion is signaled when RTT exceeds both (baseline RTT * this value) and
    /// <see cref="CongestionRttFloor"/>. Default: 2.
    /// </summary>
    public int CongestionRttMultiplier { get; init; } = 2;

    /// <summary>
    /// Absolute RTT below which the multiplier test never signals congestion, so low-RTT links
    /// (baseline of a few ms) are not downshifted by ordinary jitter. Matches the fixed 50 ms
    /// threshold this test used before baseline tracking existed: RTTs at or under the floor are
    /// never an RTT spike, exactly as before. Default: 50 ms.
    /// </summary>
    public TimeSpan CongestionRttFloor { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// RTT multiplier for the severe-congestion tier (the speed-mode/fps/resolution cascade in
    /// <c>AdaptationPolicy</c>). Same baseline-relative treatment as
    /// <see cref="CongestionRttMultiplier"/>: severe requires RTT above both (baseline RTT * this
    /// value) and <see cref="SevereCongestionRttFloor"/>, replacing the historical fixed 100 ms
    /// test that pinned every link with a propagation RTT above it (satellite, cross-continent)
    /// permanently in the severe tier. Default: 3 — stricter than the ordinary congestion
    /// multiplier of 2, because the cascade is a bigger hammer than a bitrate downshift.
    /// </summary>
    public int SevereCongestionRttMultiplier { get; init; } = 3;

    /// <summary>
    /// Absolute RTT below which the severe-tier multiplier test never fires, so low-RTT links are
    /// not cascaded by ordinary jitter. Matches the fixed 100 ms threshold the severe test used
    /// before baseline tracking: RTTs at or under the floor are never severe, exactly as before.
    /// Default: 100 ms.
    /// </summary>
    public TimeSpan SevereCongestionRttFloor { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// RTT multiplier gating the stability walk-up (resolution/fps/speed recovery in
    /// <c>AdaptationPolicy</c>): recovery requires RTT below (baseline RTT * this value), or below
    /// the historical 40 ms absolute gate. Without the baseline-relative term a link whose
    /// propagation RTT exceeds 40 ms could cascade down (e.g. on a loss episode) but never walk
    /// back up — the mirror image of the fixed severe threshold above. Default: 1.25 (RTT within
    /// 25% of baseline), deliberately tighter than the congestion multiplier so recovery needs a
    /// genuinely settled link.
    /// </summary>
    public double RecoveryRttMultiplier { get; init; } = 1.25;

    /// <summary>
    /// Jitter multiplier for the queueing early warning. Rising delay variation without loss
    /// means queues are building — the case where backing off aggressively is exactly wrong — so
    /// a jitter spike only tempers <em>increases</em>: the bitrate upshift holds and the
    /// resolution/fps/speed walk-up waits until jitter settles; it never triggers a cut. Spike =
    /// jitter above both (tracked baseline jitter * this value) and
    /// <see cref="JitterSpikeFloor"/>, the same baseline-relative treatment as
    /// <see cref="CongestionRttMultiplier"/> so links with naturally high jitter are not
    /// permanently held. Default: 2.
    /// </summary>
    public int JitterSpikeMultiplier { get; init; } = 2;

    /// <summary>
    /// Absolute jitter below which the spike test never fires, so ordinary wobble on a
    /// low-jitter link (baseline of a millisecond or two) is not treated as queueing. Default:
    /// 10 ms — roughly half a frame interval at 60 fps.
    /// </summary>
    public TimeSpan JitterSpikeFloor { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Maximum bytes allowed in RTP send queue before flow-control throttling.
    /// Default: 100 KB.
    /// </summary>
    public int MaxPendingRtpBytes { get; init; } = 100_000;

    /// <summary>
    /// Window size for stability tracking (frame count).
    /// At 60 fps, 60 frames = 1 second of observation.
    /// Default: 60.
    /// </summary>
    public int StabilityWindowFrames { get; init; } = 60;

    /// <summary>
    /// Supported output widths for resolution adaptation (in descending order).
    /// Default: [1920, 1600, 1280, 960, 640].
    /// </summary>
    public int[] SupportedWidths { get; init; } = [1920, 1600, 1280, 960, 640];

    /// <summary>
    /// Supported output heights for resolution adaptation (in descending order).
    /// Default: [1080, 900, 720, 540, 360].
    /// </summary>
    public int[] SupportedHeights { get; init; } = [1080, 900, 720, 540, 360];

    /// <summary>
    /// Supported frame rates for adaptation (in descending order).
    /// Default: [60, 30, 15].
    /// </summary>
    public int[] SupportedFps { get; init; } = [60, 30, 15];

    /// <summary>
    /// Minimum frame count between IDR (I-frame) emissions.
    /// Default: 60 (at 60 fps, once per second).
    /// </summary>
    public int IdrCooldownFrames { get; init; } = 60;

    /// <summary>
    /// Minimum frame count between resolution or frame rate adaptations.
    /// Default: 30 (at 60 fps, once per 0.5 seconds).
    /// </summary>
    public int AdaptationCooldownFrames { get; init; } = 30;

    /// <summary>
    /// Logging level for rate control decisions.
    /// Default: Trace (verbose, for debugging).
    /// </summary>
    public LogLevel LogLevel { get; init; } = LogLevel.Trace;
}
