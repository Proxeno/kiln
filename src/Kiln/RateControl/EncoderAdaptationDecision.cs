namespace Kiln.RateControl;

/// <summary>
/// The rate controller's decision for one frame: what the encoder should do in response to the
/// current network and pipeline state.
/// </summary>
/// <param name="TargetBitrateBps">Target bitrate in bits per second.</param>
/// <param name="MaxFrameBytes">Largest size, in bytes, a single frame should reach to stay within
/// the bitrate budget.</param>
/// <param name="TargetFps">Frame rate the caller should pace capture at.</param>
/// <param name="Width">Recommended output width in pixels.</param>
/// <param name="Height">Recommended output height in pixels.</param>
/// <param name="BaseQp">Slice QP for this frame (0-51; lower is higher quality).</param>
/// <param name="ForceIdr">True when this frame should be encoded as an IDR.</param>
/// <param name="EnableIntraRefresh">True when recovery was requested while an IDR is in cooldown,
/// so gradual intra refresh should be used instead.</param>
/// <param name="SpeedMode">Speed mode the encoder should use.</param>
public sealed record EncoderAdaptationDecision(
    int TargetBitrateBps,
    int MaxFrameBytes,
    int TargetFps,
    int Width,
    int Height,
    int BaseQp,
    bool ForceIdr,
    bool EnableIntraRefresh,
    EncoderSpeedMode SpeedMode);
