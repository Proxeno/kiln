namespace Kiln.RateControl;

/// <summary>
/// Output of the rate controller decision logic. Specifies what the encoder should do
/// in response to current network and pipeline state.
/// </summary>
public sealed record EncoderAdaptationDecision(
    /// <summary>Target bitrate for this frame/period (bps).</summary>
    int TargetBitrateBps,

    /// <summary>Maximum byte size a single frame should not exceed to stay within bitrate budget.</summary>
    int MaxFrameBytes,

    /// <summary>Desired frame rate (fps) for encoding output (1-120).</summary>
    int TargetFps,

    /// <summary>Desired output video width in pixels.</summary>
    int Width,

    /// <summary>Desired output video height in pixels.</summary>
    int Height,

    /// <summary>Base quantization parameter for this frame (0-51, lower = higher quality).</summary>
    int BaseQp,

    /// <summary>Should the encoder emit an IDR (I-frame) on the next encoded frame?</summary>
    bool ForceIdr,

    /// <summary>Should the encoder use intra refresh instead of full IDR for error recovery?</summary>
    bool EnableIntraRefresh,

    /// <summary>Encoder speed/quality trade-off mode to use.</summary>
    EncoderSpeedMode SpeedMode
);
