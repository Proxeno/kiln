namespace Kiln.RateControl;

/// <summary>
/// Captures current encoder pipeline and frame encoding state metrics.
/// These measurements help the rate controller understand encoding efficiency and load.
/// </summary>
public sealed record EncoderPipelineStats(
    /// <summary>How long the most recent frame took to encode (wall-clock time).</summary>
    TimeSpan LastEncodeDuration,

    /// <summary>Rolling average encode duration (e.g., over last 30 frames at 60fps = 0.5 seconds).</summary>
    TimeSpan AverageEncodeDuration,

    /// <summary>Number of raw input frames currently pending encoding in the pipeline.</summary>
    int PendingInputFrames,

    /// <summary>Number of fully encoded frames waiting to be sent to the network.</summary>
    int PendingEncodedFrames,

    /// <summary>Cumulative count of input frames dropped due to encoding backpressure.</summary>
    int DroppedInputFrames,

    /// <summary>Cumulative count of encoded frames dropped (e.g., when network queue is full).</summary>
    int DroppedEncodedFrames,

    /// <summary>Byte size of the most recent encoded frame (after any packetization/NALU headers).</summary>
    int LastEncodedFrameBytes,

    /// <summary>Quality parameter (quantization) of the last encoded frame (0-51, lower = higher quality).</summary>
    int LastFrameQp,

    /// <summary>Was the last frame an IDR (I-frame / instantaneous decoder refresh)?</summary>
    bool LastFrameWasIdr,

    /// <summary>Estimated motion complexity of the current content (0.0-1.0, 0=static, 1=maximum motion).</summary>
    double MotionComplexity,

    /// <summary>Estimated texture complexity of the current content (0.0-1.0, 0=simple, 1=complex).</summary>
    double TextureComplexity,

    /// <summary>Did a scene change occur in the current frame?</summary>
    bool SceneChangeDetected
);
