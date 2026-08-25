namespace Kiln.RateControl;

/// <summary>
/// Optional host-measured pipeline load figures a caller can feed into
/// <see cref="Kiln.H264StreamingSession.EncodeFrame"/>. These are the
/// <see cref="EncoderPipelineStats"/> fields the session cannot derive from the bitstream itself:
/// wall-clock encode durations and the caller's queue/drop accounting. When omitted, the session
/// reports them as zero and the controller's encode-backpressure escalation simply never fires —
/// the deterministic default.
/// <para>
/// Determinism note: the session guarantees identical bitstreams for identical inputs, and these
/// timings are inputs. Feeding live <see cref="System.Diagnostics.Stopwatch"/> measurements makes
/// the produced stream depend on them (that is their purpose — shedding encoder load); replaying a
/// session bit-exactly then requires replaying the recorded timings, not re-measuring.
/// </para>
/// </summary>
public sealed record EncoderPipelineTimings(
    /// <summary>How long the most recent frame took to encode (wall clock).</summary>
    TimeSpan LastEncodeDuration,

    /// <summary>Rolling average encode duration over the caller's chosen window.</summary>
    TimeSpan AverageEncodeDuration,

    /// <summary>Raw input frames currently pending encoding in the caller's pipeline.</summary>
    int PendingInputFrames = 0,

    /// <summary>Encoded frames waiting to be sent to the network.</summary>
    int PendingEncodedFrames = 0,

    /// <summary>Cumulative input frames dropped due to encoding backpressure.</summary>
    int DroppedInputFrames = 0,

    /// <summary>Cumulative encoded frames dropped (e.g. network queue full).</summary>
    int DroppedEncodedFrames = 0);
