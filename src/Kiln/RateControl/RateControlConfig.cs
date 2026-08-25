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
