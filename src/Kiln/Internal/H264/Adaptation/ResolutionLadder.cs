namespace Kiln.Internal.H264.Adaptation;

/// <summary>
/// Ordered ladder of output resolutions (highest → lowest) for adaptive down/up-scaling under
/// network pressure. Steps are taken one rung at a time so quality changes are gradual.
/// </summary>
public sealed class ResolutionLadder
{
    /// <summary>A rung on the ladder. <see cref="Name"/> is descriptive only — rung lookup matches on
    /// <see cref="Width"/>/<see cref="Height"/> so callers may pass a probe with any name.</summary>
    public sealed record Resolution(int Width, int Height, string Name)
    {
        public int PixelCount => Width * Height;
    }

    private static readonly Resolution[] DefaultLadder =
    [
        new Resolution(1920, 1080, "1080p"),
        new Resolution(1600, 900, "900p"),
        new Resolution(1280, 720, "720p"),
        new Resolution(960, 540, "540p"),
        new Resolution(640, 360, "360p"),
    ];

    private readonly Resolution[] _ladder;

    public ResolutionLadder(params Resolution[] customLadder)
    {
        _ladder = customLadder.Length > 0 ? customLadder : DefaultLadder;
    }

    // Match a rung by dimensions only. The previous implementation used Array.IndexOf, which compares
    // the whole record (including Name) — so a probe like new Resolution(1920,1080,"current") never
    // matched the "1080p" rung and adaptation silently never fired.
    private int IndexOf(Resolution current)
    {
        for (var i = 0; i < _ladder.Length; i++)
            if (_ladder[i].Width == current.Width && _ladder[i].Height == current.Height)
                return i;
        return -1;
    }

    /// <summary>Next lower rung, or null if already at the bottom (or the rung is unknown).</summary>
    public Resolution? GetLowerResolution(Resolution current)
    {
        var index = IndexOf(current);
        if (index < 0 || index >= _ladder.Length - 1)
            return null;
        return _ladder[index + 1];
    }

    /// <summary>Next higher rung, or null if already at the top (or the rung is unknown).</summary>
    public Resolution? GetHigherResolution(Resolution current)
    {
        var index = IndexOf(current);
        if (index <= 0)
            return null;
        return _ladder[index - 1];
    }

    /// <summary>Coarse rung pick for a target bitrate. Index is clamped to the ladder length so custom
    /// (shorter) ladders never index out of range.</summary>
    public Resolution GetResolutionForBitrate(int bitrateBps, int fps)
    {
        var idx =
            bitrateBps >= 8_000_000 ? 0 :
            bitrateBps >= 5_000_000 ? 1 :
            bitrateBps >= 3_000_000 ? 2 :
            bitrateBps >= 1_500_000 ? 3 : 4;
        return _ladder[Math.Min(idx, _ladder.Length - 1)];
    }

    public IReadOnlyList<Resolution> GetAllResolutions() => _ladder;
}
