using Microsoft.Extensions.Logging;
using Kiln.RateControl;

namespace Kiln.Internal.H264.Queue;

/// <summary>
/// Encapsulates the logic for deciding whether a frame being encoded should be dropped
/// in favor of a newer frame that has arrived.
/// </summary>
public sealed class FrameDropPolicy
{
    private readonly ILogger<FrameDropPolicy> _logger;

    /// <summary>
    /// Default stale frame threshold in milliseconds.
    /// At 60 fps, a frame is considered stale after ~3 frame periods (50ms).
    /// </summary>
    private const int DefaultStaleThresholdMs = 50;

    public FrameDropPolicy(ILogger<FrameDropPolicy> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Determine whether the current frame being encoded should be dropped
    /// in favor of a newer frame that has arrived.
    /// </summary>
    /// <param name="currentFrameArrivalTimeMs">The arrival time of the frame currently being encoded.</param>
    /// <param name="currentTimeMs">The current wall-clock time (e.g., Environment.TickCount64).</param>
    /// <param name="pendingInputFrames">Number of frames still waiting in the input queue.</param>
    /// <param name="staleThresholdMs">Threshold in milliseconds for considering a frame stale. Default: 50ms.</param>
    /// <returns>
    /// True if the current frame is stale AND there are newer frames pending.
    /// False otherwise.
    /// </returns>
    public bool ShouldDropCurrentFrame(
        long currentFrameArrivalTimeMs,
        long currentTimeMs,
        int pendingInputFrames,
        int staleThresholdMs = DefaultStaleThresholdMs)
    {
        // Calculate frame age in milliseconds
        var frameAgeMs = currentTimeMs - currentFrameArrivalTimeMs;

        // Frame is considered stale if it's older than the threshold
        var isFrameStale = frameAgeMs > staleThresholdMs;

        // Only drop if:
        // 1. Frame is stale (old enough that a newer frame likely exists)
        // 2. There are pending frames (backlog indicates sustained pressure)
        var shouldDrop = isFrameStale && pendingInputFrames > 1;

        if (shouldDrop)
        {
            _logger.LogInformation(
                "Dropping stale input frame. Age: {AgeMs}ms, Pending: {Pending}, Threshold: {ThresholdMs}ms",
                frameAgeMs,
                pendingInputFrames,
                staleThresholdMs
            );
        }

        return shouldDrop;
    }
}
