namespace Kiln.RateControl;

/// <summary>
/// A snapshot of network and transport state, supplied by the caller's transport for each
/// rate-control decision. Callers with sparse feedback (for example periodic RTCP) pass their most
/// recent snapshot every frame, but must set the one-shot signals
/// (<paramref name="PictureLossIndication"/>, <paramref name="FullIntraRequest"/>) on exactly one
/// frame each.
/// </summary>
/// <param name="EstimatedAvailableBitrateBps">Current estimated available bitrate from the
/// transport's bandwidth estimator (bps) — GCC, transport-cc, REMB or similar. The controller
/// treats it as a hard ceiling on the target bitrate: when the estimate falls below the current
/// target, the target drops to it immediately, while the loss/RTT heuristics keep driving
/// reductions below it and recovery after the estimate rises again still walks up through the
/// stability window. Non-positive (the default 0) means "no estimate supplied": the controller uses
/// its loss/RTT heuristics alone.</param>
/// <param name="PacketLossRatio">Fraction of packets lost, from 0.0 to 1.0.</param>
/// <param name="RoundTripTime">Round-trip time to the client. Zero or negative means "not measured".</param>
/// <param name="Jitter">Network jitter — delay variation from the transport (RFC 3550 interarrival
/// jitter, or RTT variation). Rising jitter without loss is queueing building rather than a
/// bandwidth collapse, so the controller uses it as an early warning that tempers increases: a
/// jitter spike (above both the tracked baseline *
/// <see cref="RateControlConfig.JitterSpikeMultiplier"/> and
/// <see cref="RateControlConfig.JitterSpikeFloor"/>) holds the bitrate upshift and defers the
/// resolution/fps/speed walk-up until it settles; it never triggers a cut. Zero or negative (the
/// default) means "not measured" and has no effect.</param>
/// <param name="PendingRtpBytes">Bytes waiting in the RTP send queue.</param>
/// <param name="NackCount">NACK requests received from the client in this observation
/// period.</param>
/// <param name="PictureLossIndication">True when the client signalled picture loss (PLI) in this
/// observation period.</param>
/// <param name="FullIntraRequest">True when the client sent a full intra request (FIR) in this
/// observation period. Takes priority over <paramref name="PictureLossIndication"/>.</param>
/// <param name="ClientDecodeDelay">Measured or estimated client-side decode/render delay. A client
/// that cannot decode within the frame interval is a decode-capacity problem, not network
/// congestion, so the controller answers with the complexity cascade (speed mode → fps →
/// resolution; see <see cref="RateControlConfig.ClientDecodeDelayBudgetFactor"/>) and never with a
/// bitrate cut — lowering bitrate does not help a decoder that cannot keep up. Null or non-positive
/// (the default) means "not measured" and has no effect.</param>
public sealed record EncoderNetworkFeedback(
    int EstimatedAvailableBitrateBps,
    double PacketLossRatio,
    TimeSpan RoundTripTime,
    TimeSpan Jitter,
    int PendingRtpBytes,
    int NackCount,
    bool PictureLossIndication,
    bool FullIntraRequest,
    TimeSpan? ClientDecodeDelay);
