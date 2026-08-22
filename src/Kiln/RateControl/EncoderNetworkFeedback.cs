namespace Kiln.RateControl;

/// <summary>
/// Captures current network and transport layer state for adaptive rate control decisions.
/// All measurements are point-in-time snapshots and may vary from frame to frame.
/// </summary>
public sealed record EncoderNetworkFeedback(
    /// <summary>Current estimated available bitrate from network (bps).</summary>
    int EstimatedAvailableBitrateBps,

    /// <summary>Fraction of packets lost on the transport layer (0.0-1.0).</summary>
    double PacketLossRatio,

    /// <summary>Round-trip time to client (typically milliseconds).</summary>
    TimeSpan RoundTripTime,

    /// <summary>Network jitter or variation in RTT (typically milliseconds).</summary>
    TimeSpan Jitter,

    /// <summary>Number of bytes pending transmission in RTP/send queue.</summary>
    int PendingRtpBytes,

    /// <summary>Count of NACK (negative acknowledgment) requests received from client.</summary>
    int NackCount,

    /// <summary>Did the client signal picture loss (PLI) on this observation period?</summary>
    bool PictureLossIndication,

    /// <summary>Did the client request a full intra-refresh (FIR) on this observation period?</summary>
    bool FullIntraRequest,

    /// <summary>Measured or estimated client-side decode/render delay (if available).</summary>
    TimeSpan? ClientDecodeDelay
);
