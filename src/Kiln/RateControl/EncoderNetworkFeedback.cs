namespace Kiln.RateControl;

/// <summary>
/// Captures current network and transport layer state for adaptive rate control decisions.
/// All measurements are point-in-time snapshots and may vary from frame to frame.
/// </summary>
public sealed record EncoderNetworkFeedback(
    /// <summary>
    /// Current estimated available bitrate from the transport's bandwidth estimator (bps) — GCC,
    /// transport-cc, REMB or similar. The controller treats it as a hard ceiling on the target
    /// bitrate: when the estimate falls below the current target, the target drops to it
    /// immediately, while the loss/RTT heuristics keep driving reductions below it and recovery
    /// after the estimate rises again still walks up through the stability window. Non-positive
    /// (the default 0) means "no estimate supplied": the controller uses its loss/RTT heuristics
    /// alone.
    /// </summary>
    int EstimatedAvailableBitrateBps,

    /// <summary>Fraction of packets lost on the transport layer (0.0-1.0).</summary>
    double PacketLossRatio,

    /// <summary>Round-trip time to client (typically milliseconds).</summary>
    TimeSpan RoundTripTime,

    /// <summary>
    /// Network jitter — delay variation from the transport (RFC 3550 interarrival jitter, or RTT
    /// variation). Rising jitter without loss is queueing building rather than a bandwidth
    /// collapse, so the controller uses it as an early warning that tempers increases: a jitter
    /// spike (above both the tracked baseline * <see cref="RateControlConfig.JitterSpikeMultiplier"/>
    /// and <see cref="RateControlConfig.JitterSpikeFloor"/>) holds the bitrate upshift and defers
    /// the resolution/fps/speed walk-up until it settles; it never triggers a cut. Zero or
    /// negative (the default) means "not measured" and has no effect.
    /// </summary>
    TimeSpan Jitter,

    /// <summary>Number of bytes pending transmission in RTP/send queue.</summary>
    int PendingRtpBytes,

    /// <summary>Count of NACK (negative acknowledgment) requests received from client.</summary>
    int NackCount,

    /// <summary>Did the client signal picture loss (PLI) on this observation period?</summary>
    bool PictureLossIndication,

    /// <summary>Did the client request a full intra-refresh (FIR) on this observation period?</summary>
    bool FullIntraRequest,

    /// <summary>
    /// Measured or estimated client-side decode/render delay. A client that cannot decode within
    /// the frame interval is a decode-capacity problem, not network congestion, so the controller
    /// answers with the complexity cascade (speed mode → fps → resolution; see
    /// <see cref="RateControlConfig.ClientDecodeDelayBudgetFactor"/>) and never with a bitrate
    /// cut — lowering bitrate does not help a decoder that cannot keep up. Null or non-positive
    /// (the default) means "not measured" and has no effect.
    /// </summary>
    TimeSpan? ClientDecodeDelay
);
