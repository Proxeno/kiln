namespace Kiln.RateControl;

/// <summary>
/// Internal mutable state maintained by the rate controller across decision calls.
/// Not exposed externally; used by LowLatencyRateController to track current encoder configuration.
/// </summary>
public sealed class RateControlState
{
    /// <summary>Current target bitrate (bps).</summary>
    public int TargetBitrateBps { get; set; }

    /// <summary>Current base quality parameter (0-51).</summary>
    public int BaseQp { get; set; }

    /// <summary>Current target frame rate (fps).</summary>
    public int TargetFps { get; set; }

    /// <summary>Current output width (pixels).</summary>
    public int Width { get; set; }

    /// <summary>Current output height (pixels).</summary>
    public int Height { get; set; }

    /// <summary>Current encoder speed/quality mode.</summary>
    public EncoderSpeedMode SpeedMode { get; set; }

    /// <summary>Counter tracking how many frames have passed without congestion detection.</summary>
    public int StableFrameCounter { get; set; } = 0;

    /// <summary>Counter tracking how many frames exceeded the max frame size budget.</summary>
    public int FrameSizeOvershoots { get; set; } = 0;

    /// <summary>
    /// Tracked baseline RTT in milliseconds for the congestion multiplier test
    /// (<see cref="RateControlConfig.CongestionRttMultiplier"/>). 0 until the first positive RTT
    /// sample arrives; from then on it snaps down to the fastest sample seen and drifts up by
    /// 1/256 of the gap per decision, so a genuinely changed route becomes the new baseline over a
    /// few seconds while a transient spike barely moves it. Updated once per
    /// <see cref="LowLatencyRateController.Decide"/> from caller-supplied feedback only — no
    /// wall-clock input, so identical feedback sequences produce identical decisions.
    /// </summary>
    public double BaselineRttMs { get; set; } = 0.0;

    /// <summary>
    /// Tracked baseline jitter in milliseconds for the queueing early warning
    /// (<see cref="RateControlConfig.JitterSpikeMultiplier"/>). Same
    /// <see cref="NetworkSignalBaseline"/> math and determinism contract as
    /// <see cref="BaselineRttMs"/>: updated once per <see cref="LowLatencyRateController.Decide"/>
    /// from caller-supplied feedback only, 0 until the first positive jitter sample.
    /// </summary>
    public double BaselineJitterMs { get; set; } = 0.0;
}
