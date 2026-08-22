namespace Kiln.RateControl;

/// <summary>
/// Encoder speed/quality trade-off mode. Controls the balance between output compression
/// and encoding latency, allowing adaptive adjustment under load.
/// </summary>
public enum EncoderSpeedMode
{
    /// <summary>Highest compression, slowest encoding. Use when bitrate is abundant and latency is flexible.</summary>
    HighQuality = 0,

    /// <summary>Default balanced mode. Recommended for most cloud-gaming scenarios.</summary>
    Balanced = 1,

    /// <summary>Faster encoding at the cost of compression. Use under moderate load or when latency is critical.</summary>
    Fast = 2,

    /// <summary>Fastest encoding with minimal compression. Use only under severe load when latency is paramount.</summary>
    VeryFast = 3
}
