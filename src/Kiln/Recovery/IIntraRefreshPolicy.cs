namespace Kiln.Recovery;

/// <summary>
/// Interface for intra refresh support.
/// Intra refresh spreads recovery (slice-level refresh) over multiple frames
/// to provide gradual error recovery without emitting a full IDR frame.
/// Full implementation is deferred; this is the interface contract.
/// </summary>
public interface IIntraRefreshPolicy
{
    /// <summary>
    /// Enable slice-level intra refresh.
    /// </summary>
    void Enable();

    /// <summary>
    /// Disable slice-level intra refresh.
    /// </summary>
    void Disable();

    /// <summary>
    /// Estimate how many frames it takes to refresh all slices.
    /// </summary>
    int EstimatedRecoveryFrames { get; }

    /// <summary>
    /// Get metrics on refresh progress (refreshed slices, total slices).
    /// </summary>
    (int RefreshedSlices, int TotalSlices) GetProgress();

    /// <summary>
    /// Reset refresh state (e.g., on scene change or IDR emission).
    /// </summary>
    void Reset();
}
