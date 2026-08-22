using Kiln.RateControl;

namespace Kiln.Internal.H264.Adaptation;

/// <summary>
/// Output of <see cref="AdaptationPolicy"/>: the recommended output geometry, frame rate, and encoder
/// speed mode for the next frame, plus the reason the decision changed (empty when nothing changed).
/// </summary>
public sealed record AdaptationDecision(
    int Width,
    int Height,
    int Fps,
    EncoderSpeedMode SpeedMode,
    string Reason);
