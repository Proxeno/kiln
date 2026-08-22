using Microsoft.Extensions.Logging;
using Kiln.RateControl;

namespace Kiln.Recovery;

/// <summary>
/// Manages IDR (I-frame) size budget constraints.
/// IDR frames can be larger than normal P-frames, but must still respect bitrate limits.
/// </summary>
public sealed class IdrBudget
{
    private readonly RateControlConfig _config;
    private readonly ILogger<IdrBudget> _logger;

    /// <summary>
    /// IDR budget multiplier: allows IDR frames to be 2x larger than average frame size.
    /// </summary>
    private const double IdrBudgetMultiplier = 2.0;

    /// <summary>
    /// Constructs an IDR budget calculator with the given configuration and logger.
    /// </summary>
    /// <param name="config">Rate control configuration.</param>
    /// <param name="logger">Logger for observability and debugging.</param>
    public IdrBudget(RateControlConfig config, ILogger<IdrBudget> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Calculate the maximum bytes an IDR frame can use while respecting bitrate constraints.
    /// IDR frames can be larger than normal P-frames by a configured multiplier.
    /// </summary>
    /// <param name="maxFrameBytes">Maximum byte size for a normal P-frame.</param>
    /// <returns>Maximum byte size allowed for an IDR frame.</returns>
    public int CalculateMaxIdrBytes(int maxFrameBytes)
    {
        var maxIdrBytes = (int)(maxFrameBytes * IdrBudgetMultiplier);

        _logger.LogTrace(
            "IDR budget: maxFrameBytes={max}, idrBudget={idrMax}",
            maxFrameBytes,
            maxIdrBytes
        );

        return maxIdrBytes;
    }

    /// <summary>
    /// Detect if an encoded IDR frame exceeded its byte budget.
    /// </summary>
    /// <param name="encodedBytes">Actual number of bytes in the encoded IDR frame.</param>
    /// <param name="maxIdrBytes">Maximum allowed IDR frame size.</param>
    /// <returns>True if the IDR frame exceeded the budget, false otherwise.</returns>
    public bool IdrFrameExceededBudget(int encodedBytes, int maxIdrBytes)
    {
        var exceeded = encodedBytes > maxIdrBytes;
        if (exceeded)
        {
            _logger.LogWarning(
                "IDR frame exceeded budget: {encoded} > {max}",
                encodedBytes,
                maxIdrBytes
            );
        }
        return exceeded;
    }
}
