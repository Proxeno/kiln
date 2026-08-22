using Microsoft.Extensions.Logging;

namespace Kiln.Recovery;

/// <summary>
/// Stub implementation of IIntraRefreshPolicy.
/// Provides a no-op implementation for testing and deferred feature completion.
/// Full intra refresh logic will be implemented in a later phase.
/// </summary>
public sealed class IntraRefreshPolicyStub : IIntraRefreshPolicy
{
    private readonly ILogger<IntraRefreshPolicyStub> _logger;

    /// <summary>
    /// Constructs an intra refresh policy stub with logging.
    /// </summary>
    /// <param name="logger">Logger for observability and debugging.</param>
    public IntraRefreshPolicyStub(ILogger<IntraRefreshPolicyStub> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Enable intra refresh (stub implementation).
    /// </summary>
    public void Enable()
    {
        _logger.LogInformation("Intra refresh enabled (stub - not yet implemented)");
    }

    /// <summary>
    /// Disable intra refresh (stub implementation).
    /// </summary>
    public void Disable()
    {
        // Stub: no-op
    }

    /// <summary>
    /// Estimated number of frames to refresh all slices (stub value).
    /// </summary>
    public int EstimatedRecoveryFrames => 30;

    /// <summary>
    /// Get refresh progress (stub implementation returns zero progress).
    /// </summary>
    /// <returns>A tuple of (refreshed slices, total slices). Stub returns (0, 0).</returns>
    public (int RefreshedSlices, int TotalSlices) GetProgress()
    {
        return (0, 0);
    }

    /// <summary>
    /// Reset refresh state (stub implementation).
    /// </summary>
    public void Reset()
    {
        _logger.LogTrace("Intra refresh reset");
    }
}
