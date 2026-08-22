namespace Kiln.Internal.H264.Adaptation;

/// <summary>Ordered ladder of frame rates (highest → lowest) for adaptive frame-rate scaling.</summary>
public sealed class FpsLadder
{
    private readonly int[] _fps;

    public FpsLadder(params int[] customLadder)
    {
        _fps = customLadder.Length > 0 ? customLadder : [60, 30, 15];
    }

    /// <summary>Next lower frame rate, or null if already at the bottom (or the value is unknown).</summary>
    public int? GetLowerFps(int current)
    {
        var index = Array.IndexOf(_fps, current);
        if (index < 0 || index >= _fps.Length - 1)
            return null;
        return _fps[index + 1];
    }

    /// <summary>Next higher frame rate, or null if already at the top (or the value is unknown).</summary>
    public int? GetHigherFps(int current)
    {
        var index = Array.IndexOf(_fps, current);
        if (index <= 0)
            return null;
        return _fps[index - 1];
    }

    /// <summary>Coarse frame-rate pick for a target bitrate.</summary>
    public int GetFpsForBitrate(int bitrateBps) =>
        bitrateBps >= 4_000_000 ? 60 : bitrateBps >= 2_000_000 ? 30 : 15;

    public IReadOnlyList<int> GetAllFps() => _fps;
}
