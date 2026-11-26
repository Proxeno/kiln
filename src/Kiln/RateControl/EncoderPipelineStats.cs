namespace Kiln.RateControl;

/// <summary>
/// Encoder and pipeline measurements for the previous frame, read by the rate controller.
/// <see cref="Kiln.H264StreamingSession"/> builds this itself; callers driving
/// <see cref="LowLatencyRateController"/> directly construct it per frame.
/// </summary>
/// <param name="LastEncodeDuration">How long the most recent frame took to encode (wall-clock
/// time).</param>
/// <param name="AverageEncodeDuration">Rolling average encode duration, for example over the last
/// 30 frames. The controller compares it with the frame interval to detect an overloaded
/// encoder.</param>
/// <param name="PendingInputFrames">Number of raw input frames currently pending encoding in the
/// pipeline.</param>
/// <param name="PendingEncodedFrames">Number of fully encoded frames waiting to be sent to the
/// network.</param>
/// <param name="DroppedInputFrames">Cumulative count of input frames dropped due to encoding
/// backpressure.</param>
/// <param name="DroppedEncodedFrames">Cumulative count of encoded frames dropped (e.g., when
/// network queue is full).</param>
/// <param name="LastEncodedFrameBytes">Size in bytes of the most recent encoded access
/// unit.</param>
/// <param name="LastFrameQp">Slice QP of the last encoded frame (0-51; lower is higher
/// quality).</param>
/// <param name="LastFrameWasIdr">True when the last encoded frame was an IDR.</param>
/// <param name="MotionComplexity">Estimated motion complexity of the content (0.0-1.0, 0=static,
/// 1=maximum motion). <see cref="Kiln.H264StreamingSession"/> fills this from the encoder's
/// measured value for the last encoded frame — mean integer-pel motion-vector magnitude over inter
/// MBs (see <see cref="Kiln.H264BaselineEncoder.LastFrameMotionComplexity"/>).</param>
/// <param name="TextureComplexity">Estimated texture complexity of the content (0.0-1.0, 0=simple,
/// 1=complex). <see cref="Kiln.H264StreamingSession"/> fills this from the encoder's measured value
/// for the last encoded frame — mean log₂ MB luma activity (see
/// <see cref="Kiln.H264BaselineEncoder.LastFrameTextureComplexity"/>).</param>
/// <param name="SceneChangeDetected">True when the last encoded frame was a scene change: a P frame
/// that coded most of its macroblocks as intra (see
/// <see cref="Kiln.H264BaselineEncoder.LastFrameSceneChange"/>). The recovery policy answers with
/// an IDR on the next frame, unless an IDR is in cooldown.</param>
public sealed record EncoderPipelineStats(
    TimeSpan LastEncodeDuration,
    TimeSpan AverageEncodeDuration,
    int PendingInputFrames,
    int PendingEncodedFrames,
    int DroppedInputFrames,
    int DroppedEncodedFrames,
    int LastEncodedFrameBytes,
    int LastFrameQp,
    bool LastFrameWasIdr,
    double MotionComplexity,
    double TextureComplexity,
    bool SceneChangeDetected);
